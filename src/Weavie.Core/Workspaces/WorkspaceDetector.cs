using Weavie.Core.FileSystem;
using Weavie.Core.TestRunning;

namespace Weavie.Core.Workspaces;

/// <summary>
/// Walks a workspace once (a bounded, shallow BFS — the walk the suggestion probe used to own), classifies
/// every language present against <see cref="WorkspacePresetCatalog"/>, and composes the workspace's setup
/// command and test profile: per-language restores chained and cd-wrapped by subdirectory, test rules unioned
/// in catalog order. Pure and deterministic — zero model tokens. See <c>docs/concepts/workspace-autoconfig.md</c>.
/// </summary>
public static class WorkspaceDetector {
	private const int MaxDepth = 2;  // scan the root + 2 levels of subdirectories (Weavie keeps src/web two levels down)
	private const int MaxDirs = 256; // hard ceiling on directories visited

	private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase) {
		".git", "node_modules", "bin", "obj", "target", "dist",
	};

	private static readonly HashSet<string> ManifestNames = new(StringComparer.OrdinalIgnoreCase) {
		"package.json", "pnpm-lock.yaml", "Cargo.toml", "go.mod", "go.work", "pyproject.toml", "Makefile",
	};

	private static readonly string[] ManifestExtensions = [".slnx", ".sln", ".csproj"];

	/// <summary>
	/// Detects the languages under <paramref name="workspaceRoot"/> and composes the settings to write. Returns
	/// <see cref="WorkspaceDetection.None"/>-shaped results (empty command / rules) for a workspace with no
	/// recognized preset, while still reporting <see cref="WorkspaceDetection.HasManifest"/> for the card gate.
	/// </summary>
	public static WorkspaceDetection Detect(string workspaceRoot, IFileSystem fileSystem) {
		ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
		ArgumentNullException.ThrowIfNull(fileSystem);

		var visited = Walk(workspaceRoot, fileSystem);
		bool hasManifest = visited.Any(dir => dir.Files.Any(IsManifest));
		string[] allFiles = [.. visited.SelectMany(dir => dir.Files.Select(file => Path.Combine(dir.Dir, file)))];

		var setupParts = new List<string>();
		var rules = new List<TestRule>();
		var languages = new List<string>();
		foreach (var preset in WorkspacePresetCatalog.All) {
			var marker = Shallowest(visited, preset);
			if (marker is not { } found) {
				continue;
			}

			string rel = RelativeDir(workspaceRoot, found.Dir);
			// A manifest in a subdirectory whose name carries shell metacharacters (or spaces) can't be cd-wrapped
			// into an auto-run command safely — the command runs unattended on session create. Decline it (the card
			// then offers the Claude flow) rather than quote across three shells or risk injection from a clone.
			if (rel != "." && !IsShellSafePath(rel)) {
				continue;
			}

			var result = preset.Detect(new DetectionContext {
				WorkspaceRoot = workspaceRoot,
				MarkerDirectory = found.Dir,
				MarkerFiles = found.Files,
				AllFiles = allFiles,
				FileSystem = fileSystem,
			});

			bool contributed = false;
			if (!string.IsNullOrEmpty(result.SetupCommand)) {
				setupParts.Add(CdWrap(rel, result.SetupCommand));
				contributed = true;
			}

			foreach (var rule in result.TestRules) {
				rules.Add(CdWrapRule(rel, rule));
				contributed = true;
			}

			if (contributed) {
				languages.Add(preset.DisplayName);
			}
		}

		return new WorkspaceDetection {
			HasManifest = hasManifest,
			SetupCommand = setupParts.Count > 0 ? string.Join(" && ", setupParts) : null,
			TestRules = rules,
			ConfiguredLanguages = languages,
		};
	}

	// Bounded shallow BFS collecting each visited directory's file names, in visitation (shallowest-first) order.
	private static List<(string Dir, IReadOnlyList<string> Files)> Walk(string root, IFileSystem fileSystem) {
		var result = new List<(string, IReadOnlyList<string>)>();
		var queue = new Queue<(string Path, int Depth)>();
		queue.Enqueue((root, 0));
		int visited = 0;
		while (queue.Count > 0 && visited < MaxDirs) {
			var (dir, depth) = queue.Dequeue();
			visited++;
			var files = new List<string>();
			foreach (var entry in fileSystem.EnumerateDirectory(dir)) {
				if (entry.IsDirectory) {
					if (depth < MaxDepth && !SkipDirs.Contains(entry.Name)) {
						queue.Enqueue((Path.Combine(dir, entry.Name), depth + 1));
					}
				} else {
					files.Add(entry.Name);
				}
			}

			result.Add((dir, files));
		}

		return result;
	}

	// The shallowest visited directory whose files match one of the preset's markers (BFS order = shallowest first).
	private static (string Dir, IReadOnlyList<string> Files)? Shallowest(
		List<(string Dir, IReadOnlyList<string> Files)> visited, WorkspacePreset preset) {
		foreach (var dir in visited) {
			if (dir.Files.Any(file => preset.Markers.Any(marker => MarkerMatches(file, marker)))) {
				return dir;
			}
		}

		return null;
	}

	private static bool MarkerMatches(string fileName, string marker) =>
		marker.StartsWith("*.", StringComparison.Ordinal)
			? fileName.EndsWith(marker[1..], StringComparison.OrdinalIgnoreCase)
			: string.Equals(fileName, marker, StringComparison.OrdinalIgnoreCase);

	private static bool IsManifest(string name) =>
		ManifestNames.Contains(name) || ManifestExtensions.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

	private static string RelativeDir(string root, string dir) {
		string rel = Path.GetRelativePath(root, dir);
		return string.IsNullOrEmpty(rel) || rel == "." ? "." : rel;
	}

	// A relative path safe to interpolate unquoted into a cd in POSIX sh, cmd.exe, and PowerShell alike: ASCII
	// letters/digits and path/word punctuation only. Excludes spaces and every shell metacharacter, so no quoting
	// (which differs per shell) is needed and a maliciously-named directory can't inject into the composed command.
	private static bool IsShellSafePath(string path) =>
		path.Length > 0 && path.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '\\' or '.' or '_' or '-');

	// Anchor a command to its manifest's subdirectory so package managers resolve from the right place. A subshell
	// keeps the cd from leaking into later chained steps; setup runs via sh -c / cmd.exe /c, which honor it.
	private static string CdWrap(string relativeDir, string command) =>
		relativeDir == "." ? command : $"(cd {relativeDir} && {command})";

	private static TestRule CdWrapRule(string relativeDir, TestRule rule) =>
		relativeDir == "." ? rule : rule with {
			RunOne = CdWrap(relativeDir, rule.RunOne),
			RunFile = CdWrap(relativeDir, rule.RunFile),
		};
}
