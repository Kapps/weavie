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
	private const int MaxDepth = 2;       // scan the root + 2 levels of subdirectories
	private const int MaxDirs = 256;      // hard ceiling on directories visited (a non-manifest repo's "no card")

	private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase) {
		".git", "node_modules", "bin", "obj", "target", "dist",
	};

	private static readonly HashSet<string> ManifestNames = new(StringComparer.OrdinalIgnoreCase) {
		"package.json", "pnpm-lock.yaml", "Cargo.toml", "go.mod", "pyproject.toml", "Makefile",
	};

	private static readonly string[] ManifestExtensions = [".slnx", ".sln", ".csproj"];

	private readonly SuggestionRegistry _registry;
	private readonly SettingsStore _settings;
	private readonly IFileSystem _fileSystem;
	private readonly string _workspaceRoot;
	private readonly SuggestionDismissals _dismissals;
	private readonly Action<IReadOnlyList<SuggestionDefinition>> _push;
	private readonly TimeSpan _probeTimeout;
	private readonly Lock _gate = new();
	private readonly HashSet<string> _snoozed = new(StringComparer.Ordinal);

	private bool _hasManifest;
	private IReadOnlyList<SuggestionDefinition> _current = [];

	/// <summary>
	/// Creates the service and kicks off the off-the-hot-path manifest probe. <paramref name="probeTimeout"/>
	/// bounds that probe, failing open (card shown) if the scan doesn't finish in time.
	/// </summary>
	public SuggestionService(
		SuggestionRegistry registry,
		SettingsStore settings,
		IFileSystem fileSystem,
		string workspaceRoot,
		SuggestionDismissals dismissals,
		TimeSpan probeTimeout,
		Action<IReadOnlyList<SuggestionDefinition>> push) {
		ArgumentNullException.ThrowIfNull(registry);
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
		ArgumentNullException.ThrowIfNull(dismissals);
		ArgumentNullException.ThrowIfNull(push);
		_registry = registry;
		_settings = settings;
		_fileSystem = fileSystem;
		_workspaceRoot = workspaceRoot;
		_dismissals = dismissals;
		_probeTimeout = probeTimeout;
		_push = push;
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
		var scan = Task.Run(() => HasManifest(_workspaceRoot));
		var winner = await Task.WhenAny(scan, Task.Delay(_probeTimeout)).ConfigureAwait(false);

		// Fail open: only a completed scan that found nothing yields false. A timeout (a tree too slow or large to
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

	private bool HasManifest(string root) {
		var queue = new Queue<(string Path, int Depth)>();
		queue.Enqueue((root, 0));
		int visited = 0;
		while (queue.Count > 0 && visited < MaxDirs) {
			var (dir, depth) = queue.Dequeue();
			visited++;
			foreach (var entry in _fileSystem.EnumerateDirectory(dir)) {
				if (entry.IsDirectory) {
					if (depth < MaxDepth && !SkipDirs.Contains(entry.Name)) {
						queue.Enqueue((Path.Combine(dir, entry.Name), depth + 1));
					}
				} else if (IsManifest(entry.Name)) {
					return true;
				}
			}
		}

		return false;
	}

	private static bool IsManifest(string name) =>
		ManifestNames.Contains(name) ||
		ManifestExtensions.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
}
