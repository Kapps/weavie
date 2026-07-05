using Weavie.Core.FileSystem;
using Weavie.Core.TestRunning;

namespace Weavie.Core.Workspaces;

/// <summary>
/// The read-only context a <see cref="WorkspacePreset.Detect"/> function sees: the workspace root, the
/// directory the preset's marker was found in (its shallowest match), that directory's file names, and the
/// filesystem seam to read manifest content (<c>package.json</c>, <c>.csproj</c>).
/// </summary>
public sealed record DetectionContext {
	/// <summary>The workspace's root directory (absolute).</summary>
	public required string WorkspaceRoot { get; init; }

	/// <summary>The absolute directory where this preset's shallowest marker was found (may equal the root).</summary>
	public required string MarkerDirectory { get; init; }

	/// <summary>The file names directly in <see cref="MarkerDirectory"/> (lockfiles, which <c>.csproj</c>, etc.).</summary>
	public required IReadOnlyList<string> MarkerFiles { get; init; }

	/// <summary>Absolute paths of every file the walk visited — lets a preset inspect files outside the marker directory (e.g. a test <c>.csproj</c> under a solution-rooted repo).</summary>
	public required IReadOnlyList<string> AllFiles { get; init; }

	/// <summary>The filesystem seam, to read manifest content.</summary>
	public required IFileSystem FileSystem { get; init; }
}

/// <summary>
/// One language's contribution to auto-config: its raw (un-cd-wrapped) package-restore command and the test
/// rules it derived. Empty <see cref="TestRules"/> means the language was recognized but its test convention
/// couldn't be identified (a gap the workspace-setup card is left to hand to Claude).
/// </summary>
public sealed record PresetResult {
	/// <summary>The package-restore shell command (e.g. <c>pnpm install</c>), relative to <see cref="DetectionContext.MarkerDirectory"/>.</summary>
	public required string SetupCommand { get; init; }

	/// <summary>The derived test rules (raw templates; the detector cd-wraps them). Empty when no runner was recognized.</summary>
	public required IReadOnlyList<TestRule> TestRules { get; init; }
}

/// <summary>
/// A curated, hardcoded language preset: the build markers that detect it plus a pure function that reads the
/// repo to produce its package-restore command and test rules. Mirrors <c>LanguageServerCatalog</c>. See
/// <c>docs/concepts/workspace-autoconfig.md</c>.
/// </summary>
public sealed record WorkspacePreset {
	/// <summary>The stable id, e.g. <c>typescript</c>.</summary>
	public required string Id { get; init; }

	/// <summary>The human-readable name, used in the auto-config toast (e.g. <c>C#</c>).</summary>
	public required string DisplayName { get; init; }

	/// <summary>The build markers that detect this language; a leading <c>*.</c> matches by extension (<c>*.csproj</c>), else by exact name.</summary>
	public required IReadOnlyList<string> Markers { get; init; }

	/// <summary>Reads the <see cref="DetectionContext"/> to produce this language's setup command and test rules. Pure — no side effects.</summary>
	public required Func<DetectionContext, PresetResult> Detect { get; init; }
}
