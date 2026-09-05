using System.Text.Json;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Layout;

/// <summary>Who initiated a layout change — for diagnostics and so subscribers can react by origin.</summary>
public enum LayoutSource {
	/// <summary>A user gesture in the web UI (drag, focus, close).</summary>
	User,

	/// <summary>An MCP <c>setLayout</c> call from Claude.</summary>
	Mcp,

	/// <summary>The reconciler adjusting the document on load.</summary>
	Reconcile,
}

/// <summary>A pane-layout change raised to subscribers (off the UI thread).</summary>
public readonly record struct LayoutChange(LayoutDocument Document, LayoutSource Source);

/// <summary>The outcome of a layout mutation — applied state plus a human-readable summary for MCP.</summary>
public sealed record LayoutResult(bool Applied, string Summary);

/// <summary>Thrown when a proposed layout can't be used: unknown pane kinds, or no panes at all.</summary>
public sealed class LayoutValidationException : Exception {
	/// <summary>Creates the exception with a human-readable <paramref name="message"/>.</summary>
	public LayoutValidationException(string message) : base(message) {
	}

	/// <summary>Creates the exception with a <paramref name="message"/> and inner cause.</summary>
	public LayoutValidationException(string message, Exception innerException) : base(message, innerException) {
	}
}

/// <summary>
/// Loads, reconciles, persists, and broadcasts the window-layout document at <c>~/.weavie/layout.json</c> —
/// the single entry point for all writers: user/Claude pane edits (<see cref="SetPanes"/>,
/// <see cref="DismissPane"/>, reconciled and web-notified) and host-only window geometry (<see cref="SetWindow"/>,
/// not notified). Writes are atomic; a malformed file is backed up to <c>layout.json.bad</c> and reset. See
/// <c>docs/specs/layout.md</c>.
/// </summary>
public sealed class LayoutStore : JsonDocumentStore {
	private readonly PaneRegistry _registry;
	private LayoutDocument _current;

	/// <summary>Creates a store over <paramref name="path"/> (default <c>~/.weavie/layout.json</c>), loading and reconciling now.</summary>
	/// <param name="fileSystem">The filesystem the document persists through.</param>
	/// <param name="registry">The pane registry the document reconciles against.</param>
	/// <param name="path">The backing file, or <c>null</c> for the default.</param>
	public LayoutStore(IFileSystem fileSystem, PaneRegistry registry, string? path)
		: base(fileSystem, path ?? WeaviePaths.LayoutFile) {
		ArgumentNullException.ThrowIfNull(registry);
		_registry = registry;
		_current = LayoutPanes.Default(registry);
		Load();
	}

	/// <summary>Raised (off the UI thread) when the pane layout changes and the web should re-render.</summary>
	public event Action<LayoutChange>? Changed;

	/// <summary>The current reconciled document. Never null.</summary>
	public LayoutDocument Current {
		get { lock (Gate) { return _current; } }
	}

	/// <summary>
	/// Replaces the pane tree (and optional focus), preserving window geometry and compatibility bookkeeping,
	/// then reconciles, persists, and notifies. Throws <see cref="LayoutValidationException"/> for unknown
	/// pane kinds or a tree with no panes.
	/// </summary>
	public LayoutResult SetPanes(LayoutNode root, string? focused, LayoutSource source) {
		ArgumentNullException.ThrowIfNull(root);
		LayoutChange change;
		lock (Gate) {
			var unknown = UnknownKinds(root, _registry);
			if (unknown.Count > 0) {
				throw new LayoutValidationException($"unknown pane kind(s): {string.Join(", ", unknown)}");
			}

			if (!HasPane(root)) {
				throw new LayoutValidationException("layout must contain at least one pane");
			}

			var candidate = _current with { Root = root, Focused = focused ?? _current.Focused };
			Reconcile(candidate);
			PersistLocked();
			change = new LayoutChange(_current, source);
		}

		Changed?.Invoke(change);
		return new LayoutResult(true, "layout updated");
	}

	/// <summary>Records that the user explicitly closed pane <paramref name="kind"/>: removes it and tombstones it so it isn't reinjected.</summary>
	public LayoutResult DismissPane(string kind, LayoutSource source) {
		ArgumentException.ThrowIfNullOrEmpty(kind);
		LayoutChange change;
		lock (Gate) {
			var dismissed = _current.Dismissed.Contains(kind)
				? _current.Dismissed
				: [.. _current.Dismissed, kind];
			var stripped = LayoutTree.Filter(
				_current.Root,
				pane => !string.Equals(pane.Kind, kind, StringComparison.Ordinal)) ?? _current.Root;
			Reconcile(_current with { Root = stripped, Dismissed = dismissed });
			PersistLocked();
			change = new LayoutChange(_current, source);
		}

		Changed?.Invoke(change);
		return new LayoutResult(true, $"closed {kind}");
	}

	/// <summary>
	/// Updates host-owned window geometry, preserving the pane tree. Persists but does not raise
	/// <see cref="Changed"/> — only the host cares about window bounds, and it is the caller.
	/// </summary>
	public void SetWindow(WindowState? window) {
		lock (Gate) {
			if (_current.Window == window) {
				return;
			}

			_current = _current with { Window = window };
			PersistLocked();
		}
	}

	/// <summary>Subscribes <paramref name="handler"/> to pane-layout changes; dispose to unsubscribe.</summary>
	public IDisposable Subscribe(Action<LayoutChange> handler) {
		ArgumentNullException.ThrowIfNull(handler);
		Changed += handler;
		return new Subscription(() => Changed -= handler);
	}

	/// <inheritdoc/>
	protected override void Restore(string? text) {
		if (text is null) {
			_current = LayoutPanes.Default(_registry);
			return;
		}

		if (!LayoutSerialization.TryDeserialize(text, out var parsed, out string? error) || parsed is null) {
			throw new JsonException(error);
		}

		if (Reconcile(parsed)) {
			PersistLocked();
		}
	}

	/// <inheritdoc/>
	protected override string Render() => LayoutSerialization.Serialize(_current);

	/// <summary>Seeds <c>layout.json</c> with the default layout, so the file always exists once a window opened.</summary>
	protected override void Establish() {
		Restore(null);
		PersistLocked();
	}

	// Reconciles a candidate into the current document, reporting its notes; true when it changed the candidate.
	private bool Reconcile(LayoutDocument candidate) {
		var outcome = LayoutReconciler.Reconcile(candidate, _registry);
		foreach (string note in outcome.Notes) {
			Report(note);
		}

		_current = outcome.Document;
		return outcome.Mutated;
	}

	private static List<string> UnknownKinds(LayoutNode node, PaneRegistry registry) {
		var kinds = new HashSet<string>(StringComparer.Ordinal);
		CollectKinds(node, kinds);
		return [.. kinds.Where(k => !registry.IsKnown(k))];
	}

	private static void CollectKinds(LayoutNode node, HashSet<string> into) {
		switch (node) {
			case PaneNode pane:
				into.Add(pane.Kind);
				break;
			case SplitNode split:
				foreach (var child in split.Children) {
					CollectKinds(child, into);
				}

				break;
		}
	}

	private static bool HasPane(LayoutNode node) =>
		node switch {
			PaneNode => true,
			SplitNode split => split.Children.Any(HasPane),
			_ => false,
		};

	private sealed class Subscription(Action dispose) : IDisposable {
		private Action? _dispose = dispose;

		public void Dispose() {
			_dispose?.Invoke();
			_dispose = null;
		}
	}
}
