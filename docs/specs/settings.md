# Settings

Status: accepted, not yet implemented
Last updated: 2026-06-15

Settings is the first concrete instance of the
[Claude-facing capability registry](../concepts/mcp-registry.md) concept: configuration values are
*declared* in Core and surfaced to the embedded Claude as MCP tools, so the user can change them by
talking to Claude.

Weavie has no persistent configuration today — every knob is an environment variable read once at
startup (`WEAVIE_WORKSPACE`, `WEAVIE_SHELL`/`SHELL`, `WEAVIE_CLAUDE`, plus dev/debug toggles). This
spec introduces a user-level settings system that is:

- **OS-portable but dev-friendly** — one file at `~/.weavie/settings.toml` on every platform.
- **Schema-driven** — settings are *declared* in code (type, default, description, validation), so
  one registry powers defaults, validation, env-var overrides, the MCP tool surface, and the
  natural-language "tell Claude to change it" flow.
- **Env-overridable** — every setting has a derived `WEAVIE_*` env var that wins over the file.
- **Editable conversationally** — the embedded `claude` changes settings through MCP tools on the
  IDE server that already exists; no new process, no CLI (for now).
- **Reactive** — a setting change is detected and acted on live (e.g. changing the shell reopens
  the terminal), not just stored.

## Goals

1. Persist user-level settings across runs in an OS-appropriate, easy-to-edit location.
2. Make every setting overridable by an environment variable for dev/CI.
3. Let the user change settings by talking to the embedded Claude
   (e.g. *"set my weavie shell to nushell"*).
4. React to changes immediately where it makes sense (reopen the terminal on a shell change).
5. Validate strictly — reject bad values loudly; never silently swallow or "fix" them.
6. Lay a foundation future plugins (and the commands registry) can extend.

## Non-goals (deferred)

- **`weavie config` CLI verb.** Wanted eventually, but there's no `weavie` on PATH today (the app
  is a GUI exe). The CLI will be a thin wrapper over the same Core store, so deferring it costs
  nothing architecturally. MCP is the only editing surface in this milestone.
- **Workspace/project-level settings** (`.weavie/settings.toml` in the repo). The resolution order
  leaves a slot for it; not built yet.
- **In-app settings UI** (web panel over the bridge). Later.
- **Secrets.** Settings are plaintext. API keys / tokens do **not** belong here; a separate
  secret-storage mechanism is out of scope.

## Storage location

A single resolution rule on all platforms:

```
~/.weavie/settings.toml
```

Resolved from the user profile / home directory
(`Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)`), correct on Windows, macOS, and
Linux. Chosen over OS-standard config dirs (`%APPDATA%`, `~/Library/Application Support`,
`$XDG_CONFIG_HOME`) because it is trivial to `cd ~/.weavie` and hand-edit, identical everywhere, and
mirrors `~/.claude` — the tool Weavie weaves. The `~/.weavie/` directory is also the natural home
for future per-user state; this spec only defines `settings.toml`.

## File format — TOML

TOML, parsed and written with [Tomlyn](https://github.com/xoofx/Tomlyn) (NuGet, MIT — one new
dependency on `Weavie.Core`). Chosen over JSON/JSONC for hand-editability:

- Native `#` comments and section grouping (`[plugins.acme-linter]`) that maps cleanly onto plugin
  subtrees.
- **No backslash-escaping for Windows paths** — literal (single-quoted) strings:
  `claude.path = 'C:\Users\me\claude.exe'`.
- Tomlyn parses into a **syntax tree (`DocumentSyntax`) that preserves comments and formatting on
  round-trip**, which gives us comment preservation essentially for free.

```toml
# weavie settings — ask the embedded Claude to "list weavie settings" for all keys

# Shell for the plain terminal pane
terminal.shell = "nu"

# Path to the claude binary (auto-detected if unset)
claude.path = 'C:\Users\me\claude.exe'

[plugins.acme-linter]   # unknown subtrees are preserved verbatim on write
severity = "error"
```

### Comments: description-above-the-line

Reads and writes go through Tomlyn's `DocumentSyntax` so existing comments and formatting survive a
round-trip. On write, the store additionally **injects a known setting's registry `description` as a
`#` comment directly above its line when that line has no leading comment** — so the file
self-documents as keys are first written, without clobbering a comment the user wrote themselves.
Unknown/plugin keys are never annotated and their trivia is left untouched.

Deferred refinement: re-propagating a changed registry description onto keys that *already* carry a
(possibly user-edited) comment. Not worth the surprise for now.

### Atomic writes

Serialize the `DocumentSyntax` to text, write `settings.toml.tmp`, then atomically replace
`settings.toml`, so a crash mid-write never corrupts the file. A malformed existing file is a loud
error surfaced to the host log (and, for `setSetting`, back to Claude) — never silently reset; the
last-good in-memory values are retained.

## The settings registry

Settings are declared in code. The registry is the single source of truth for what exists, its type,
default, docs, validation, env var, and how a change applies. Core registers its settings at
startup; plugins (future) contribute additional definitions the same way.

```csharp
public enum SettingKind { String, Bool, Int, Path, Enum }

// How a changed value takes effect — reported to Claude by setSetting, and the contract the
// host's change-reaction wiring honors.
public enum ApplyMode {
    Live,            // observers reflect it immediately, nothing restarts
    ReopensTerminal, // the affected terminal pane(s) restart to apply (shell)
    NextSession,     // existing sessions keep the old value; the next one started picks it up
    RestartRequired, // needs a full app restart
}

public sealed record SettingDefinition {
    public required string Key { get; init; }                 // "terminal.shell"
    public required SettingKind Kind { get; init; }
    public required string Description { get; init; }         // → MCP + the file's "# " comment
    public IReadOnlyList<string> Aliases { get; init; } = []; // "shell", "my shell" — NL hints
    public IReadOnlyList<string>? AllowedValues { get; init; }// for Enum
    public object? Default { get; init; }                     // static default…
    public Func<object?>? ComputeDefault { get; init; }       // …or computed (platform auto-detect)
    public Func<object?, ValidationResult>? Validate { get; init; }
    public ApplyMode Apply { get; init; } = ApplyMode.NextSession;

    // Derived: "terminal.shell" -> "WEAVIE_TERMINAL_SHELL". No per-setting registration needed.
    public string EnvVar => "WEAVIE_" + Key.ToUpperInvariant().Replace('.', '_');
}
```

### Env-var override convention

Every setting's env var is **derived** from its key — `WEAVIE_` + the key uppercased with `.` → `_`:

| key              | env var                  |
|------------------|--------------------------|
| `workspace`      | `WEAVIE_WORKSPACE`       |
| `terminal.shell` | `WEAVIE_TERMINAL_SHELL`  |
| `claude.path`    | `WEAVIE_CLAUDE_PATH`     |

There are **no legacy aliases** — Weavie is not launched, so the existing names (`WEAVIE_SHELL`,
`WEAVIE_CLAUDE`) are renamed to the derived form and their call sites updated. The OS-standard
`SHELL`/`pwsh`/`zsh` discovery is **not** an env alias — it is the *computed default* for
`terminal.shell` (the lowest-precedence layer), which is the correct place for "what shell does the
system suggest."

### Resolution order (highest wins)

```
1. environment variable   (the setting's derived WEAVIE_* name)   ← always wins; for dev/CI
2. ~/.weavie/settings.toml                                         ← what setSetting writes
3. registered default     (static Default or ComputeDefault())
   ── reserved slot 1.5: workspace .weavie/settings.toml (future) ──
```

```csharp
public ResolvedValue Resolve(string key) {
    var def = _registry.Require(key);                          // unknown key -> throw (strict)
    var env = Environment.GetEnvironmentVariable(def.EnvVar);
    if (env is not null) return new(Parse(def, env), SettingSource.Environment);
    if (_doc.TryGet(key, out var fileValue)) return new(fileValue, SettingSource.UserFile);
    return new(def.ComputeDefault?.Invoke() ?? def.Default, SettingSource.Default);
}
```

### Writes and the env-shadow warning

`setSetting` requires an **exact registered key**. Unknown keys are rejected (optionally with
near-match suggestions) — fuzzy/natural-language mapping is the LLM's job, done by reading the
catalog from `listSettings`, never by the store guessing.

Because env vars win over the file, writing a setting while its env var is set won't change the
*effective* value. Per the strict-enforcement principle, `setSetting` reports this loudly instead of
silently no-opping:

```csharp
public SetResult Set(string key, JsonElement value) {           // value arrives as JSON (MCP)
    var def = _registry.Require(key);                           // strict: unknown -> error
    var result = def.Validate?.Invoke(Coerce(def, value)) ?? ValidationResult.Ok;
    if (!result.Ok) throw new SettingValidationException(key, result.Message);   // loud
    _doc.Set(key, Coerce(def, value));                          // into the TOML syntax tree
    SaveAtomic();
    RaiseChanged(key, ...);                                      // -> reaction hub (below)
    var shadow = Environment.GetEnvironmentVariable(def.EnvVar);
    return new SetResult {
        Written       = true,
        ShadowedByEnv = shadow is null ? null : def.EnvVar,     // "wrote it, but $ENV overrides"
        Apply         = def.Apply,                              // "your terminal will reopen"
    };
}
```

Note the boundary: **MCP speaks JSON, the file is TOML.** `setSetting`'s `value` is a JSON value;
the store coerces it to the declared type via the registry and writes it into the TOML document. The
file format never leaks into the Claude-facing contract.

## Reacting to changes — the change hub

`SettingsStore` is the change hub. It raises a typed event that any component can subscribe to and
react to however it needs:

```csharp
public readonly record struct SettingChange(string Key, object? OldValue, object? NewValue, SettingSource Source);

public event Action<SettingChange>? SettingChanged;
public IDisposable Subscribe(string key, Action<SettingChange> handler);   // convenience: one key
```

`SettingChanged` fires from **two** sources:

1. **`setSetting`** (explicit, via MCP).
2. **A `FileSystemWatcher` on `settings.toml`**, *debounced* (~250 ms) and *parse-guarded*: a
   half-typed hand-edit never triggers a reaction; the store reacts only once the file settles into
   a clean parse, then diffs against the current resolved values and raises one change per key.

Reactions run off the raising thread, so subscribers marshal to the UI thread the same way
`HostBridge.PostToWeb` already does (`BeginInvoke` on Windows, `InvokeOnMainThread` on macOS).

### Shell change → reopen the terminal

The host subscribes to `terminal.shell` and reopens the shell pane(s), reusing the existing
ready/start handshake:

```
setSetting("terminal.shell","nu")   (or a settled hand-edit)
  → store writes + raises SettingChanged("terminal.shell")
  → host subscriber, on the UI thread, calls shellController.Restart():
       dispose the PTY, post {type:"term-reset", session:"shell"} to the web
  → web TerminalView for "shell" clears its xterm + re-sends term-ready(cols,rows)
  → controller.Start() spawns a fresh PTY, resolving the shell from the store
```

The claude pane is a different setting (`claude.path`) and is untouched. Reopening **kills that
pane's scrollback and any running command** — acceptable for an explicitly requested change, and the
debounce/parse-guard keeps a hand-edit from thrashing it. New plumbing required:

- `TerminalController.Restart()` (dispose + emit `term-reset`).
- A `term-reset` inbound message handled in the web `bridge.ts` / `TerminalView` (clear xterm,
  re-emit `term-ready`).

(If auto-reopening on *passive* hand-edits ever proves annoying, a later refinement can downgrade
the file-watch path to `NextSession` while keeping explicit `setSetting` on reopen. Not done now.)

## Initial registered settings

These three replace the current real env vars. The resolution logic currently inline in the two
`TerminalController`s (`ResolveWorkspace`/`ResolveShellLauncher`/`ResolveClaudeLauncher`) moves into
the registered settings' `ComputeDefault`/`Validate`, so Windows and macOS share one path.

| key              | kind   | default (computed)                                             | apply            | replaces            |
|------------------|--------|----------------------------------------------------------------|------------------|---------------------|
| `workspace`      | Path   | user profile dir; must exist                                   | RestartRequired  | `WEAVIE_WORKSPACE`  |
| `terminal.shell` | String | Win: `pwsh` → `powershell`; macOS: `$SHELL` → `/bin/zsh`       | ReopensTerminal  | `WEAVIE_SHELL`/`SHELL` |
| `claude.path`    | Path   | `claude` on PATH → native-installer location → bare `claude`   | NextSession      | `WEAVIE_CLAUDE`     |

`terminal.shell` carries `Aliases = ["shell", "my shell", "terminal shell"]` and
`Validate = resolve-on-PATH` so *"set my weavie shell to nushell"* maps cleanly and a bogus shell is
rejected at set time. `claude.path` is `NextSession` (not `ReopensTerminal`) on purpose — reopening
the claude pane would destroy the running conversation, so a new binary applies to the next claude
session.

**Dev/debug toggles stay raw env vars** and are intentionally *not* registered — not user-facing,
should not appear in `listSettings`: `WEAVIE_PTY_LOG`, `WEAVIE_AUTOBENCH`, `WEAVIE_FPSPROBE`,
`WEAVIE_SHOT_*`, `WEAVIE_DEMO_DIFF`, `WEAVIE_DEBUG_INPUT`.

## MCP tools (the editing surface)

Three tools are added to the existing `McpServer` (`Weavie.Core/Mcp/McpServer.cs`), appended to
`ToolsListJson` and dispatched in `HandleToolCallAsync`. See the
[capability registry concept](../concepts/mcp-registry.md) for how these are generated from the
registry rather than hand-written.

### `listSettings`

No input. Returns the live catalog — what Claude reads to map natural language to an exact key, so it
carries descriptions, aliases, current value, source, and default:

```json
{ "settings": [
  { "key": "terminal.shell", "type": "string",
    "description": "Shell for the plain terminal pane",
    "aliases": ["shell", "my shell", "terminal shell"],
    "value": "nu", "source": "userFile", "default": "pwsh", "apply": "reopensTerminal" }
] }
```

### `getSetting`

Input `{ "key": "terminal.shell" }` → the resolved value and where it came from.

### `setSetting`

Input `{ "key": "terminal.shell", "value": "nu" }`. Validates against the registry; writes the user
file; returns a confirmation including the apply note and any env-shadow warning. Its description
instructs the model to **call `listSettings` first** to find the exact key rather than guessing.

### Worked flow: *"set my weavie shell to nushell"*

1. Claude calls `listSettings`, sees `terminal.shell` (aliases include "shell").
2. Claude resolves "nushell" → `nu` and calls `setSetting { key: "terminal.shell", value: "nu" }`.
3. Store validates `nu` is resolvable, writes `~/.weavie/settings.toml`, raises the change →
   the host reopens the shell pane.
4. Claude confirms: *"Set your terminal shell to nushell — I've reopened your terminal so it's
   live now."* (If `WEAVIE_TERMINAL_SHELL` were set, it would instead warn that the env var is
   overriding the file.)

## Architecture / placement

```
Weavie.Core/
  Configuration/
    SettingDefinition.cs     // the record above
    SettingsRegistry.cs      // register + Require(key) + catalog
    SettingsStore.cs         // TOML load/save (Tomlyn, atomic), Resolve, Set, watch, change hub
    CoreSettings.cs          // registers workspace / terminal.shell / claude.path
  Mcp/
    McpServer.cs             // + listSettings/getSetting/setSetting tools
```

Both hosts construct one `SettingsStore` and hand it to their `TerminalController`s and `McpServer`,
and subscribe to `terminal.shell` to drive the reopen. The per-platform resolution logic inline in
the `TerminalController`s collapses into the registered settings' `ComputeDefault`/`Validate`.

## Build sequence

1. **Core config module** — `SettingDefinition`, `SettingsRegistry`, `SettingsStore`
   (Tomlyn load/save + description-comment injection, atomic write, env+file+default resolution,
   validation, debounced parse-guarded `FileSystemWatcher`, `SettingChanged`/`Subscribe`). Register
   `workspace`, `terminal.shell`, `claude.path`; port the `TerminalController` resolution logic in;
   update both hosts to read from the store. Unit tests in `Weavie.Core.Tests` (resolution
   precedence, unknown-key + comment preservation, atomic write, validation rejects bad values,
   env-shadow reporting, change-diff on file edit).
2. **Reaction wiring** — `TerminalController.Restart()` + `term-reset` web handling; hosts subscribe
   `terminal.shell` → reopen. Verify a shell change reopens the pane.
3. **MCP tools** — `listSettings`/`getSetting`/`setSetting` on `McpServer`, wired to the store.
   Verify end-to-end by asking the embedded Claude to change the shell.

## Open questions

- **Description re-propagation** — see [Comments](#comments-description-above-the-line); deferred.
- **Plugin schema contribution** — exact mechanism (when/how a plugin registers definitions, lazy
  validation of namespaced `plugins.*` values) is left for the plugin/commands work.
