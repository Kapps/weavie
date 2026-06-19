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
/// Loads, reconciles, persists, and broadcasts the window-layout document at <c>~/.weavie/layout.json</c>.
/// The single entry point all three writers go through: the user (web) and Claude (MCP) via
/// <see cref="SetPanes"/> / <see cref="DismissPane"/>, and the host via <see cref="SetWindow"/>. Pane
/// edits run through <see cref="LayoutReconciler"/>; window-geometry edits are host-only and don't notify
/// the web. Writes are atomic; a malformed file is backed up to <c>layout.json.bad</c> and reset to the
/// default. See <c>docs/specs/layout.md</c>.
/// </summary>
public sealed class LayoutStore {
	private readonly IFileSystem _fileSystem;
	private readonly PaneRegistry _registry;
	private readonly Lock _gate = new();
	private LayoutDocument _current;

	/// <summary>Creates a store over <paramref name="path"/> (default <c>~/.weavie/layout.json</c>), loading and reconciling now.</summary>
	public LayoutStore(IFileSystem fileSystem, PaneRegistry registry, string? path) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentNullException.ThrowIfNull(registry);
		_fileSystem = fileSystem;
		_registry = registry;
		FilePath = path ?? WeaviePaths.LayoutFile;
		lock (_gate) {
			_current = LoadAndReconcileLocked();
		}
	}

	/// <summary>Raised (off the UI thread) when the pane layout changes and the web should re-render.</summary>
	public event Action<LayoutChange>? Changed;

	/// <summary>Diagnostic log line — load failures, prunes, injections, and resets.</summary>
	public event Action<string>? Log;

	/// <summary>The layout file backing this store.</summary>
	public string FilePath { get; }

	/// <summary>The current reconciled document. Never null.</summary>
	public LayoutDocument Current {
		get { lock (_gate) { return _current; } }
	}

	/// <summary>
	/// Replaces the pane tree (and optional focus), preserving window geometry and compatibility
	/// bookkeeping. Used by the web (user edits) and MCP (Claude). Reconciles, persists, and notifies.
	/// Throws <see cref="LayoutValidationException"/> for unknown pane kinds or a tree with no panes.
	/// </summary>
	public LayoutResult SetPanes(LayoutNode root, string? focused, LayoutSource source) {
		ArgumentNullException.ThrowIfNull(root);
		LayoutChange change;
		lock (_gate) {
			var unknown = UnknownKinds(root, _registry);
			if (unknown.Count > 0) {
				throw new LayoutValidationException($"unknown pane kind(s): {string.Join(", ", unknown)}");
			}

			if (!HasPane(root)) {
				throw new LayoutValidationException("layout must contain at least one pane");
			}

			var candidate = _current with { Root = root, Focused = focused ?? _current.Focused };
			var outcome = LayoutReconciler.Reconcile(candidate, _registry);
			LogNotes(outcome.Notes);
			_current = outcome.Document;
			PersistLocked(_current);
			change = new LayoutChange(_current, source);
		}

		Changed?.Invoke(change);
		return new LayoutResult(true, "layout updated");
	}

	/// <summary>Records that the user explicitly closed pane <paramref name="kind"/>: removes it and tombstones it so it isn't reinjected.</summary>
	public LayoutResult DismissPane(string kind, LayoutSource source) {
		ArgumentException.ThrowIfNullOrEmpty(kind);
		LayoutChange change;
		lock (_gate) {
			var dismissed = _current.Dismissed.Contains(kind)
				? _current.Dismissed
				: [.. _current.Dismissed, kind];
			var stripped = RemoveKind(_current.Root, kind) ?? _current.Root;
			var candidate = _current with { Root = stripped, Dismissed = dismissed };
			var outcome = LayoutReconciler.Reconcile(candidate, _registry);
			LogNotes(outcome.Notes);
			_current = outcome.Document;
			PersistLocked(_current);
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
		lock (_gate) {
			if (_current.Window == window) {
				return;
			}

			_current = _current with { Window = window };
			PersistLocked(_current);
		}
	}

	/// <summary>Subscribes <paramref name="handler"/> to pane-layout changes; dispose to unsubscribe.</summary>
	public IDisposable Subscribe(Action<LayoutChange> handler) {
		ArgumentNullException.ThrowIfNull(handler);
		Changed += handler;
		return new Subscription(() => Changed -= handler);
	}

	private LayoutDocument LoadAndReconcileLocked() {
		if (!_fileSystem.FileExists(FilePath)) {
			var seeded = LayoutPanes.Default(_registry);
			PersistLocked(seeded);
			return seeded;
		}

		string text;
		try {
			text = _fileSystem.ReadAllText(FilePath);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Log?.Invoke($"[layout] could not read {FilePath}: {ex.Message}; using default");
			return LayoutPanes.Default(_registry);
		}

		if (!LayoutSerialization.TryDeserialize(text, out var parsed, out string? error) || parsed is null) {
			Log?.Invoke($"[layout] {FilePath} is malformed ({error}); backing up to layout.json.bad and resetting");
			BackupBadFileLocked(text);
			var fresh = LayoutPanes.Default(_registry);
			PersistLocked(fresh);
			return fresh;
		}

		var outcome = LayoutReconciler.Reconcile(parsed, _registry);
		LogNotes(outcome.Notes);
		if (outcome.Mutated) {
			PersistLocked(outcome.Document);
		}

		return outcome.Document;
	}

	private void BackupBadFileLocked(string text) {
		try {
			_fileSystem.WriteAllText(FilePath + ".bad", text);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Log?.Invoke($"[layout] could not back up malformed layout: {ex.Message}");
		}
	}

	private void PersistLocked(LayoutDocument document) {
		try {
			_fileSystem.WriteAllTextAtomic(FilePath, LayoutSerialization.Serialize(document));
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Log?.Invoke($"[layout] could not persist layout: {ex.Message}");
		}
	}

	private void LogNotes(IReadOnlyList<string> notes) {
		foreach (string note in notes) {
			Log?.Invoke($"[layout] {note}");
		}
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

	private static LayoutNode? RemoveKind(LayoutNode node, string kind) {
		switch (node) {
			case PaneNode pane:
				return string.Equals(pane.Kind, kind, StringComparison.Ordinal) ? null : pane;
			case SplitNode split:
				var children = new List<LayoutNode>();
				var weights = new List<double>();
				for (int i = 0; i < split.Children.Count; i++) {
					var child = RemoveKind(split.Children[i], kind);
					if (child is not null) {
						children.Add(child);
						weights.Add(i < split.Weights.Count ? split.Weights[i] : 1.0);
					}
				}

				return children.Count switch {
					0 => null,
					1 => children[0],
					_ => split with { Children = children, Weights = weights },
				};
			default:
				return node;
		}
	}

	private sealed class Subscription(Action dispose) : IDisposable {
		private Action? _dispose = dispose;

		public void Dispose() {
			_dispose?.Invoke();
			_dispose = null;
		}
	}
}
