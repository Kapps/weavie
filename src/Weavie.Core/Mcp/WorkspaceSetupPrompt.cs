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
			propose both values together and ask me to confirm them before you persist (call the `setSetting`
			tool with the key and value). Inspect the repo freely to derive the values, but change nothing
			except these two settings.

			1. `worktree.setupCommand` — the single shell command that makes a fresh checkout ready to work in
			   (install dependencies, and a build step only if one is required before editing). Derive it from
			   the repo (package manager lockfiles, project files). If nothing is needed, propose an empty
			   string. This runs in the platform shell (POSIX `sh` on macOS/Linux, `cmd.exe` on Windows),
			   so `&&` and pipes are fine.

			2. `test.profile` — a JSON array of rules that let Weavie show run buttons on test blocks without
			   knowing any framework. Work out this repo's test files and how its tests are run (from its
			   package scripts, go.mod, project files, config), then write one rule per test convention. Each
			   rule is an object:
			     - "glob": a file glob selecting test files (e.g. "**/*.test.ts?(x)", "**/*_test.go").
			     - "symbol": a regex matched against an LSP document-symbol name; a match marks it a test, and
			       the regex's first capture group (if any) is the test's name.
			     - "runOne": a shell command template to run a single test.
			     - "runFile": a shell command template to run every test in a file.
			     - "nameSeparator" (optional): joins the captured names down nested blocks into ${name};
			       defaults to a single space. It must produce exactly what runOne's name filter expects, which
			       differs per runner — so verify it, don't assume (e.g. vitest's `-t` matches the space-joined
			       name, not a " > "-joined one).
			     - "header" (optional): a regex matched against the source just before a symbol's name — where
			       attributes/annotations/decorators sit (e.g. an xUnit "\\[(Fact|Theory)\\b") — so
			       attribute-based tests are selectable.
			   Templates support the placeholders ${file} and ${fileDir} (absolute paths) and, in "runOne",
			   ${name} (the composed test name). Weavie shell-quotes every substitution.

			   runOne/runFile run in the workspace's shell pane, whose shell is `terminal.shell` and may not be
			   bash/POSIX — so keep them to plain external commands. Avoid shell operators (`&&`, `||`, `|`, `;`)
			   unless you have confirmed that shell supports them; if a runner needs a build first, prefer
			   pointing the rule at already-built output over chaining a build into the template.

			   Some runners target a project/module, not a file (e.g. `dotnet test`, Gradle, Maven), and cannot
			   be aimed at one source file. When a repo has several such projects, write one rule per project —
			   glob-scope each to its project's files and hard-code that project's path in the template — so
			   "run file" stays scoped to the right project.

			   To confirm a rule before proposing it, you may run its runOne once and check that it selects
			   exactly the intended test (name filters and their join chars differ per runner). Don't run the
			   whole suite or a build to explore. Set `test.profile` to `[]` (explicitly) if this repo has no tests.

			When you finish, report exactly which settings you wrote and their values, note that they are stored
			per-repo in `.weavie/settings.toml`, and tell me I can re-run this setup any time with
			`/mcp__weavie__setup-workspace` or by editing that file.
			""",
	};
}
