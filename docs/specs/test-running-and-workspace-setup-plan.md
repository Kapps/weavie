# Implementation plan: test running & workspace setup

Execution plan for [test-running-and-workspace-setup.md](test-running-and-workspace-setup.md) —
read the spec first; this file adds the build order, per-milestone contracts, and gates. The design
is settled: implement it as written, don't re-open decisions. Work the milestones in order; each
ends with a gate (all listed commands green) and a commit.

## How to work

- Follow repo CLAUDE.md strictly. The ones violated most often: no optional/default parameters
  (`WV0001`; `CancellationToken` exempt), no nullable injected dependencies, no fallbacks (fail
  loudly at the user-facing surface), XML doc comments on public APIs (CS1591), files ~200 lines /
  split past 300 by responsibility, minimal comments.
- Mirror neighboring code before inventing shape: new commands copy the declaration style in
  `src/Weavie.Core/Commands/CoreCommands.cs` and `SessionCommands.cs`; new settings copy the
  definitions in `src/Weavie.Core/Configuration/CoreSettings.cs`; new suggestion mirrors
  `src/Weavie.Core/Suggestions/CoreSuggestions.cs`; new bridge messages mirror the dispatch style in
  `src/Weavie.Hosting/HostCore.WebBridge.cs`.
- Gates: C# — `dotnet test tests/Weavie.Core.Tests/Weavie.Core.Tests.csproj -c Debug` and
  `dotnet test tests/Weavie.Hosting.Tests/Weavie.Hosting.Tests.csproj -c Debug`; web (in `src/web`,
  pnpm only) — `pnpm run verify` and `pnpm run test`; e2e — `pnpm run e2e`. Full-stack tests stub
  claude at `TerminalController.ResolveClaudeLaunch` via `tools/Weavie.FakeClaude` (see
  docs/specs/integration-testing-strategy.md) — never the real model.
- After the last milestone: run the `weavie-reviewer` agent on the change set and act on findings,
  then `weavie-tester` for a `.webm` of the journeys in milestone 7.

## Fixed decisions (do not re-litigate)

- Positions from LSP `documentSymbol`; test identification and run commands are workspace data.
  No framework/language knowledge in code — and none in shipped data either (no presets).
- `${file}`/`${fileDir}` substitute **absolute** paths; all placeholder values are shell-quoted.
  POSIX single-quote escaping; double-quote escaping for PowerShell; cmd.exe gets the PowerShell
  treatment (no cmd-specific logic).
- Unset `test.profile` → no lenses + loud failure from run commands; `[]` → "repo has no tests".
  Never guess a command.
- Setup ships as an MCP prompt (`/mcp__weavie__setup-workspace`) on the registry `McpServer`;
  seeded into the Claude pane pre-filled, never auto-sent.
- Run output goes to the session's **shell** pane via `TerminalController.Write`; busy shell → fail
  with toast, never queue.

## Milestone 1 — workspace settings layer

The settings spec reserves a workspace layer (docs/specs/settings.md, "workspace" section):
`<workspaceRoot>/.weavie/settings.toml`, resolution env > workspace file > user file > default.

- `src/Weavie.Core/Configuration/SettingDefinition.cs`: add `SettingScope` enum (`User`,
  `Workspace`) and `SettingScope Scope { get; init; } = SettingScope.User` (property initializer —
  not a constructor default param — keeps WV0001 happy).
- `src/Weavie.Core/Configuration/SettingsStore.cs`: load + watch the workspace file into a layer
  between env and user; route writes by the definition's scope (`Workspace` → workspace file,
  else user file). `setSetting`'s MCP signature is unchanged. Malformed workspace file → keep
  last-good values and surface a `notify` warning (matching how the user file is handled — check
  and mirror). Split a `SettingsStore.WorkspaceLayer.cs` partial if the file passes ~300 lines.
- Migrate `worktree.setupCommand`'s definition to `Scope = Workspace` (reads still fall through to
  the user file, so existing configs keep working).
- Tests (`tests/Weavie.Core.Tests`, mirror the existing `SettingsStore` tests): precedence order,
  scope-routed writes, workspace-file watch triggers `SettingChanged`, malformed file → last-good.

Gate: Core tests green.

## Milestone 2 — test profile domain (pure Core, no wiring)

New folder `src/Weavie.Core/TestRunning/`:

- `TestProfile.cs`: `TestRule` record — `required string Glob`, `required string Symbol`,
  `required string RunOne`, `required string RunFile`, `string NameSeparator = " "` (init
  property), `string? Header` (optional rule *data*, not an optional param). `TestProfile.TryParse
  (string json, out TestProfile profile, out string error)` validates JSON shape, each regex
  compiles, glob non-empty. Distinguish parsed-empty (`[]`, valid, means "no tests") from unset.
- `TestCommandComposer.cs`: pure static — given a `TestRule`, template kind (runOne/runFile),
  absolute file path and optional composed test name, returns the command string. Shell quoting:
  `PosixQuote` (single-quote wrap, `'\''` for embedded quotes) and `PowerShellQuote` (single-quote
  wrap, `''` doubling); selection by shell comes in milestone 3. Reject unknown `${...}`
  placeholders with a clear error (no silent pass-through).
- `src/Weavie.Core/Configuration/TestSettings.cs`: registers `test.profile` (String kind, `Scope =
  Workspace`, `Validate` = `TestProfile.TryParse`). Register wherever `CoreSettings` definitions
  are registered at startup — follow that path.
- Tests: TryParse (valid multi-rule, bad regex, bad JSON, `[]`), composer substitution, quoting
  incl. quote-injection attempts in `${name}` (`it('a "b" c')`, backticks, `$(rm -rf)`), unknown
  placeholder rejection.

Gate: Core tests green.

## Milestone 3 — run commands + terminal seam

- `src/Weavie.Core/Commands/CoreCommands.cs`: declare `weavie.tests.run` (Core, args `{ file,
  name? }`), `weavie.tests.runFile` (Core, args `{ file? }` — defaults to active editor file,
  resolved web-side when invoked from palette/keybinding), `weavie.tests.runAtCursor` (Web, no
  args, `When = "editorFocused"`). Default keys: `$mod+alt+t` (runFile), `$mod+alt+r`
  (runAtCursor) — first check both against every existing default in `CoreCommands`/
  `SessionCommands` and the keybindings docs; on collision pick a free chord and note it in the
  commit message. Mirror args-schema style of existing commands (e.g. how `CloseTab`-style optional
  JSON fields are declared).
- New `src/Weavie.Hosting/HostCore.TestRun.cs` partial: handler for both Core commands, registered
  per session where `WireSession` registers the session command handlers
  (`src/Weavie.Hosting/HostCore.Sessions.cs`) so an MCP invocation from a worktree session runs in
  *that* session's shell. Behavior, in order:
  1. Read `test.profile`; unset → `CommandResult.Failure("No test profile is configured — run 'Set
     Up This Workspace' first.")`.
  2. Resolve the rule: first rule whose glob matches the file (workspace-relative match, then make
     the substituted path absolute). No match → failure naming the file.
  3. Busy check: if the shell has a foreground job, fail + `notify` error toast ("Tests not
     started: the shell is busy."). Look for an existing busy/foreground signal on
     `TerminalController`; if none exists, add the minimal one — do not skip the check.
  4. Compose (quoting selected off the effective shell setting; PowerShell/cmd → PowerShell
     quoting, else POSIX) and `session.Shell.Write(Encoding.UTF8.GetBytes(command + "\r"))`
     (`TerminalController.Write`, src/Weavie.Hosting/TerminalController.cs:461).
  5. Post the existing pane-focus message for the shell pane so the user sees the run (find how the
     web focuses panes today and reuse that message).
- Tests (`tests/Weavie.Hosting.Tests` + headless e2e): unit — no-profile failure, rule resolution,
  busy refusal; e2e journey with profile set to `echo RUN ${file}` templates (data!), invoke
  `weavie.tests.runFile` via the command path, assert the shell xterm renders `RUN /abs/path`;
  busy-shell case → toast, nothing written.

Gate: Core + Hosting tests green, e2e green.

## Milestone 4 — web lenses

New `src/web/src/tests/` (one job per file), wired from where the editor host initializes LSP:

- `test-profile.ts`: signal from `__WEAVIE_TEST_PROFILE__` (inject pre-nav next to
  `__WEAVIE_LSP__` — see `PushLspConfigToWeb` in `HostCore.WebBridge.cs`) + a `test-profile` bridge
  push re-sent on `SettingChanged` for `test.profile` (add the push in `HostCore.TestRun.cs`; add
  the web route in `src/web/src/bridge.ts`).
- `glob.ts`: glob → RegExp supporting `**`, `*`, `?`, `{a,b}`, `?(x)`. No dependency.
- `test-symbols.ts`: fetch `DocumentSymbol[]` through the vscode services layer
  (`StandaloneServices.get(ILanguageFeaturesService).documentSymbolProvider` — same deep-import
  style as the `DocumentSemanticTokensFeature` import in `src/web/src/editor/vscode-services.ts`);
  walk the tree; a symbol is a test iff the rule's `symbol` regex matches its name AND (`header`
  absent OR `header` matches the model text between `range.start` and `selectionRange.start`).
  Test name = regex captures joined along the ancestor chain with `nameSeparator`.
- `test-lens.ts`: `monaco.languages.registerCodeLensProvider({ scheme: "file" }, …)`. One `▷ Run
  (⌘⌥R)` lens per matched symbol and one `▷ Run file (⌘⌥T)` lens at the top of the file — the
  parenthesized shortcuts read the effective bindings of `weavie.tests.runAtCursor` /
  `weavie.tests.runFile` from the web command catalog via `formatKey`, never hardcoded, omitted
  when unbound. Lens click → dispatch `weavie.tests.run` with `{ file, name }` through
  `src/web/src/commands/dispatch.ts`. Fire `onDidChange` on profile change and language-client
  start (export a hook from `src/web/src/lsp/lsp-client.ts` — the first symbol request can beat
  server readiness). Model path → workspace-relative for glob matching (workspace root is in the
  LSP config — export a getter from `lsp-client.ts`).
- Register the `weavie.tests.runAtCursor` web handler (in `src/web/src/commands/registry.ts`
  pattern): innermost matched symbol containing the cursor → dispatch `weavie.tests.run`; none →
  the standard command-failure surface, not a silent no-op.
- Tests (vitest): `glob.ts` cases; matcher + nested-name composition + `header` slicing against
  these captured fixtures (assert against them verbatim):
  - tsserver: nested symbols named `describe('math') callback` / `it('adds') callback` /
    `test('subtracts') callback` — rule `symbol: "^(?:describe|it|test)\\((?:'|\")(.+?)(?:'|\")"`.
  - gopls: flat function symbols `TestAdds`, `TestSubtracts`, `helper` — rule
    `symbol: "^(Test\\w+)"`. `t.Run` subtests do not appear as symbols (known limitation, do not
    "fix").
  - csharp-ls: method symbols `Adds()`, `Subtracts(int x)`, `NotATest()`; header slice of the
    first two contains `[Fact]` / `[Theory]\n[InlineData(1)]`, third does not — rule
    `symbol: "^(\\w+)\\(", header: "\\[(Fact|Theory)\\b"`.

Gate: `pnpm run verify`, `pnpm run test`; one headless Playwright smoke (lens appears on a `.test.ts`
file when a profile is set and `typescript-language-server` is on PATH — follow the existing e2e
suite's LSP gating pattern).

## Milestone 5 — MCP prompts capability

- New `src/Weavie.Core/Mcp/McpServer.Prompts.cs` partial: `McpPrompt` record (`Name`, `Description`,
  `Text`); advertise the `prompts` capability (registry-mode server only — mirror how existing
  registry-only capabilities are gated); handle `prompts/list` and `prompts/get` per the MCP spec
  (get returns the text as a single user message).
- New `src/Weavie.Core/Mcp/WorkspaceSetupPrompt.cs`: the maintained prompt text. It must instruct
  Claude to: inspect the repo; propose `worktree.setupCommand` and a `test.profile` (teach the rule
  schema + placeholders exactly as in the spec — no framework examples); ask the user to confirm;
  persist each confirmed value via `setSetting`; write `[]` for `test.profile` when the repo has no
  tests; touch only registered settings; run nothing else; and close by listing every setting
  written, that they live in `<workspaceRoot>/.weavie/settings.toml`, and that setup can be re-run
  anytime via `/mcp__weavie__setup-workspace` or by editing that file.
- Wire the prompt list into the registry server where it's constructed (follow `IdeIntegration` /
  server construction path).
- Tests: xUnit round-trip — `prompts/list` shows the prompt, `prompts/get` returns the text;
  extend the FakeClaude-driven integration test so a scripted `setSetting` for `test.profile`
  lands in `.weavie/settings.toml` and fires `SettingChanged`.

Gate: Core + Hosting tests green.

## Milestone 6 — setup flow swap

**6a first (manual gate)**: verify in real Claude Code that a bracketed-pasted
`/mcp__weavie__setup-workspace` + Enter executes as a slash command. If it stays literal text,
seed `WorkspaceSetupPrompt`'s full text instead of the slash command — same artifact, same flow;
record the outcome in the spec's open questions.

- `src/Weavie.Core/Suggestions/CoreSuggestions.cs`: replace the worktree card with id
  `workspace.setup`, title "Set up this workspace?", `IsRelevant = ctx.HasBuildManifest &&
  (worktree.setupCommand unset || test.profile unset)`; actions: Yes → `weavie.workspace.setup`,
  Not now → Snooze, Don't ask again → DismissForever.
- `src/Weavie.Core/Commands/CoreCommands.cs`: declare `weavie.workspace.setup` (Core,
  palette-visible, "Set Up This Workspace with Claude"); delete `SuggestSetupCommand`.
- `src/Weavie.Hosting/HostCore.Suggestions.cs`: replace `SeedSetupCommandPrompt` +
  `SetupCommandPrompt` const with `SeedWorkspaceSetup` — `WriteBracketedPaste` of the slash command
  (or prompt text per 6a) into the primary session's Claude pane, never sending Enter.
- Dismissal continuity: a persisted `worktree.setupCommand` dismissal must also silence
  `workspace.setup` (one mapping where dismissals are read — don't re-nag existing users).
- Tests: suggestion relevance matrix (unset/`[]`/configured × manifest present/absent); e2e — card
  → Yes → assert the pane received the pre-fill bytes and **no** newline; scripted FakeClaude
  setSetting → card disappears on `SettingChanged`.

Gate: all C# tests + e2e green.

## Milestone 7 — docs + validation

- Update `docs/specs/suggestions.md` (worktree card → workspace.setup) and `docs/specs/settings.md`
  (workspace layer is now real). Add a one-line concept entry only if CLAUDE.md's existing lines no
  longer cover it.
- `weavie-reviewer` on the full change set; fix findings.
- `weavie-tester` journeys (one `.webm`): open a `.test.ts` file with a configured profile → lenses
  visible → click Run → command appears in the shell pane and executes; palette "Run Tests in File";
  no-profile state shows the card, Yes pre-fills the Claude pane; busy-shell refusal toast.

Gate: everything in "How to work" green; then push and update the PR body's status.
