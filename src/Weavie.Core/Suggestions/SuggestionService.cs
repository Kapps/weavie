using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Suggestions;

/// <summary>
/// Evaluates the registered suggestions against the workspace and pushes the active set. Owns the in-memory
/// snooze set, the persisted "don't ask again" store, and the bounded, memoized, fail-open manifest probe.
/// Re-evaluate on a trigger (setting changed, session created) reads the cached probe result rather than
/// re-walking the disk. See <c>docs/specs/suggestions.md</c>.
/// </summary>
public sealed class SuggestionService {
	private readonly SuggestionRegistry _registry;
	private readonly SettingsStore _settings;
	private readonly IFileSystem _fileSystem;
	private readonly string _workspaceRoot;
	private readonly SuggestionDismissals _dismissals;
	private readonly Action<IReadOnlyList<SuggestionDefinition>> _push;
	private readonly Func<bool> _probe;
	private readonly Func<int> _pendingCorrections;
	private readonly TimeSpan _probeTimeout;
	private readonly Lock _gate = new();
	private readonly HashSet<string> _snoozed = new(StringComparer.Ordinal);

	private bool _hasManifest;
	private IReadOnlyList<SuggestionDefinition> _current = [];

	/// <summary>
	/// Creates the service and kicks off the off-the-hot-path <paramref name="probe"/> — the host-supplied walk
	/// that classifies + auto-configures the workspace and reports whether it carries a build manifest.
	/// <paramref name="probeTimeout"/> bounds it, failing open (card shown) if it doesn't finish in time.
	/// <paramref name="pendingCorrections"/> supplies the correction ring's live count per evaluation.
	/// </summary>
	public SuggestionService(
		SuggestionRegistry registry,
		SettingsStore settings,
		IFileSystem fileSystem,
		string workspaceRoot,
		SuggestionDismissals dismissals,
		TimeSpan probeTimeout,
		Action<IReadOnlyList<SuggestionDefinition>> push,
		Func<bool> probe,
		Func<int> pendingCorrections) {
		ArgumentNullException.ThrowIfNull(registry);
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
		ArgumentNullException.ThrowIfNull(dismissals);
		ArgumentNullException.ThrowIfNull(push);
		ArgumentNullException.ThrowIfNull(probe);
		ArgumentNullException.ThrowIfNull(pendingCorrections);
		_registry = registry;
		_settings = settings;
		_fileSystem = fileSystem;
		_workspaceRoot = workspaceRoot;
		_dismissals = dismissals;
		_probeTimeout = probeTimeout;
		_push = push;
		_probe = probe;
		_pendingCorrections = pendingCorrections;
		_ = RunProbeAsync();
	}

	/// <summary>Re-evaluates and pushes the active set. Call on a trigger (setting changed, session created).</summary>
	public void Evaluate() {
		IReadOnlyList<SuggestionDefinition> active;
		lock (_gate) {
			var context = new SuggestionContext {
				WorkspaceRoot = _workspaceRoot,
				Settings = _settings,
				FileSystem = _fileSystem,
				HasBuildManifest = _hasManifest,
				PendingCorrectionCount = _pendingCorrections(),
			};
			active = [.. _registry.Definitions.Where(d =>
				!_snoozed.Contains(d.Id) && !IsDismissed(d) && d.IsRelevant(context))];
			_current = active;
		}

		_push(active);
	}

	// Dismissed if the card's own id — or any id it superseded — was dismissed forever (migration continuity).
	private bool IsDismissed(SuggestionDefinition definition) =>
		_dismissals.IsDismissed(definition.Id) || definition.LegacyIds.Any(_dismissals.IsDismissed);

	/// <summary>Pushes the current active set without re-evaluating (used on the page's first <c>ready</c>).</summary>
	public void PushCurrent() {
		IReadOnlyList<SuggestionDefinition> current;
		lock (_gate) {
			current = _current;
		}

		_push(current);
	}

	/// <summary>Hides <paramref name="id"/> for this app run ("not now").</summary>
	public void Snooze(string id) {
		ArgumentException.ThrowIfNullOrEmpty(id);
		lock (_gate) {
			_snoozed.Add(id);
		}

		Evaluate();
	}

	/// <summary>Dismisses <paramref name="id"/> permanently for this workspace ("don't ask again").</summary>
	public void DismissForever(string id) {
		ArgumentException.ThrowIfNullOrEmpty(id);
		_dismissals.Add(id);
		Evaluate();
	}

	private async Task RunProbeAsync() {
		var scan = Task.Run(_probe);
		var winner = await Task.WhenAny(scan, Task.Delay(_probeTimeout)).ConfigureAwait(false);

		// Fail open: only a completed probe that found nothing yields false. A timeout (a tree too slow or large to
		// finish in time) or a fault shows the dismissible card — such a repo almost certainly has dependencies.
		bool hasManifest = true;
		if (winner == scan && scan.Status == TaskStatus.RanToCompletion) {
			hasManifest = scan.Result;
		}

		lock (_gate) {
			_hasManifest = hasManifest;
		}

		Evaluate();
	}
}
