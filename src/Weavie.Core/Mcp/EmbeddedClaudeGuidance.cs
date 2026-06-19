namespace Weavie.Core.Mcp;

/// <summary>
/// The system-prompt appendix handed to the embedded <c>claude</c> (via <c>--append-system-prompt-file</c>)
/// so it understands it is running inside Weavie and prefers the <c>mcp__weavie__*</c> tools — the live
/// source of truth — over reading Weavie's on-disk config when answering "what is currently set" or
/// changing themes/settings/layout. Appended (not replacing) so claude keeps its default coding-assistant
/// identity. Surfaced only when the registry server is wired (i.e. those tools actually exist); see
/// <see cref="IdeIntegration.WriteSystemPromptFile"/>.
/// </summary>
public static class EmbeddedClaudeGuidance {
	/// <summary>The appendix text. Plain UTF-8; written to a file and referenced by path, so no shell quoting applies.</summary>
	public const string SystemPromptAppendix =
		"""
		You are running embedded in Weavie, an agentic code editor. Weavie exposes its own live state and
		capabilities to you as MCP tools named `mcp__weavie__*` — covering themes, settings, the window
		layout, and commands (named actions).

		These tools are the live source of truth for what the running app has actually loaded. When the user
		asks what is currently set (e.g. "do I have any theme overrides", "what's my font size", "what theme
		am I on") or asks you to change Weavie's themes/settings/layout or run a command, use the
		`mcp__weavie__*` tools. Discover the exact id/key with the matching list tool first (listSettings,
		listThemes, listCommands) rather than guessing.

		Do NOT answer "what is currently set" by reading Weavie's config files on disk — e.g. files under
		`~/.weavie/` such as `theme-overrides.json`, or `~/.claude.json`. Those are only the persisted layer
		and can diverge from the live app: an override may be applied in-session but not yet written, or a
		file edited without a reload. Always read live state through the `mcp__weavie__*` tools instead.
		""";
}
