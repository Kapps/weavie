using Weavie.Core.TestRunning;

namespace Weavie.Core.Workspaces;

/// <summary>
/// The result of running <see cref="WorkspaceDetector.Detect"/> over a workspace: whether it carries any build
/// manifest (drives the workspace-setup card), the composed package-restore command and unioned test rules to
/// write, and the display names of the languages that contributed (for the toast).
/// </summary>
public sealed record WorkspaceDetection {
	/// <summary>An empty result — no manifest, nothing to write. The detection of a workspace with no recognizable build files.</summary>
	public static WorkspaceDetection None { get; } = new() {
		HasManifest = false,
		SetupCommand = null,
		TestRules = [],
		ConfiguredLanguages = [],
	};

	/// <summary>Whether the workspace has any recognizable build manifest (JS/.NET/Go/Rust/Python/Make), used to gate the card.</summary>
	public required bool HasManifest { get; init; }

	/// <summary>The composed setup command (per-language restores chained with <c>&amp;&amp;</c>, cd-wrapped by subdir), or <c>null</c> if no preset matched.</summary>
	public required string? SetupCommand { get; init; }

	/// <summary>The unioned test rules across every matched language (empty when none produced rules).</summary>
	public required IReadOnlyList<TestRule> TestRules { get; init; }

	/// <summary>The display names of the languages that contributed a setup command or test rules.</summary>
	public required IReadOnlyList<string> ConfiguredLanguages { get; init; }
}
