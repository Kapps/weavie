namespace Weavie.Core.Mcp;

/// <summary>
/// The maintained text of the <c>/mcp__weavie__setup-workspace</c> prompt: it has Claude inspect the repo and
/// configure the knowledge-shaped workspace settings (<c>worktree.setupCommand</c> and <c>test.profile</c>)
/// with the user's confirmation. Teaches only the schema — Claude derives the commands from the repo, so
/// Weavie ships no framework knowledge.
/// </summary>
public static class WorkspaceSetupPrompt {
	/// <summary>The registered prompt (name, description, text).</summary>
	public static McpPrompt Prompt { get; } = new() {
		Name = "setup-workspace",
		Description = "Configure this workspace's Weavie settings (worktree setup command + test runner).",
		Text = """
			Set up this workspace's Weavie settings by inspecting the repository. Configure two settings, then
			ask me to confirm each proposed value before you persist it (call the `setSetting` tool with the key
			and value). Do not run any other commands or change anything else.

			1. `worktree.setupCommand` — the single shell command that makes a fresh checkout ready to work in
			   (install dependencies, and a build step only if one is required before editing). Derive it from
			   the repo (package manager lockfiles, project files). If nothing is needed, propose an empty string.

			2. `test.profile` — a JSON array of rules that let Weavie show run buttons on test blocks without
			   knowing any framework. Work out this repo's test files and how its tests are run (from its
			   package scripts, go.mod, project files, config), then write one rule per test convention. Each
			   rule is an object:
			     - "glob": a file glob selecting test files (e.g. "**/*.test.ts?(x)", "**/*_test.go").
			     - "symbol": a regex matched against an LSP document-symbol name; a match marks it a test, and
			       the regex's first capture group (if any) is the test's name.
			     - "runOne": a shell command template to run a single test.
			     - "runFile": a shell command template to run every test in a file.
			     - "nameSeparator" (optional): joins captured names down nested blocks (e.g. " > " for vitest);
			       defaults to a single space.
			     - "header" (optional): a regex matched against the source just before a symbol's name — where
			       attributes/annotations/decorators sit (e.g. an xUnit "\\[(Fact|Theory)\\b") — so
			       attribute-based tests are selectable.
			   Templates support the placeholders ${file} and ${fileDir} (absolute paths) and, in "runOne",
			   ${name} (the composed test name). Weavie shell-quotes every substitution.
			   Set `test.profile` to `[]` (explicitly) if this repository has no tests.

			When you finish, report exactly which settings you wrote and their values, note that they are stored
			per-repo in `.weavie/settings.toml`, and tell me I can re-run this setup any time with
			`/mcp__weavie__setup-workspace` or by editing that file.
			""",
	};
}
