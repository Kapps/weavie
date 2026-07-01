namespace Weavie.Core;

/// <summary>
/// Locations of Claude Code's own config/data, honoring <c>$CLAUDE_CONFIG_DIR</c> (else <c>~/.claude</c>) — the
/// single source of truth for where Weavie reads Claude's on-disk state (IDE lock files, conversation transcripts).
/// </summary>
public static class ClaudeConfigPaths {
	/// <summary>Claude's config directory: <c>$CLAUDE_CONFIG_DIR</c> when set, else <c>~/.claude</c>.</summary>
	public static string ConfigDirectory {
		get {
			string? configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
			return string.IsNullOrEmpty(configDir)
				? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
				: configDir;
		}
	}

	/// <summary>Where Claude files per-project conversation transcripts: <c>&lt;ConfigDirectory&gt;/projects</c>.</summary>
	public static string ProjectsDirectory => Path.Combine(ConfigDirectory, "projects");
}
