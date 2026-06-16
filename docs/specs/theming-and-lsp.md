# Theming & Language Intelligence (LSP) — Design Spec

**Status:** Draft / design only — no code yet.
**Date:** 2026-06-15
**Scope:** How Weavie does (a) editor color themes reusing the VS Code theme ecosystem, with
user-tweakable **overrides** driven via Claude/MCP, and (b) language intelligence (LSP) including
semantic highlighting — without adopting VS Code's workbench, configuration, keybindings, commands,
or extension host.

---

## 1. Goals

- **Reuse VS Code color themes**, installable from the **Open VSX** registry (`.vsix`), and have
  them render *faithfully* — including **semantic highlighting** (class vs variable vs parameter,
  the "Visual Studio" classifier experience), not just regex-based syntax coloring.
- **User-tweakable theme overrides via Claude/MCP** — "make the background pure black", "make
  everything 20% darker", "brighten the comments" — layered on top of the active theme (§6).
- **Real multi-language LSP** (diagnostics, hover, go-to-def, completion, rename, semantic tokens)
  across the languages we choose to support.
- Keep Weavie's own UI: its SolidJS two-pane chrome, its xterm terminals, its bridge, its
  (separately-built) settings + MCP capability registry.
- **Fonts are plain settings**, decoupled from color themes (see §4).

## 2. Non-goals & hard constraints (the guardrails)

These are firm. They shape every decision below.

- **No VS Code workbench / no layout control.** Weavie owns its layout and windows. Nothing in the
  theming/LSP stack may render an activity bar, sidebar, panels, editor groups, or otherwise manage
  layout. *(This is the hard no.)*
- **No configuration service override** — no `settings.json`, no settings UI from the stack. A
  separate agent is building Weavie's own settings; the stack must not compete with it.
- **No keybindings / commands / quick-pick** from the stack.
- **No extension host.** We do **not** run arbitrary VS Code extension activation JavaScript.
- Settings *storage for the selected values* (active theme, override values, font family/size/weight,
  etc.) is **out of scope** here — owned by the settings agent. This spec **does** define how *themes
  themselves* are stored (§13) and how overrides compose & apply (§6).

## 3. Two color tables — the key mental model

A VS Code theme carries **two** independent coloring tables. Faithful rendering (and the semantic
highlighting we want) needs **both**:

| Table | Driven by | Nature | Example selector → color |
| --- | --- | --- | --- |
| `tokenColors` | TextMate **grammar** (regex) | **Syntactic** — fast, no server, often coarse/guessed | `entity.name.function` → `#DCDCAA` |
| `semanticTokenColors` | **LSP server** semantic tokens | **Semantic** — the compiler's truth | `class` → `#4EC9B0`, `parameter` → `#9CDCFE` |

They **layer**: TextMate paints instantly as you type; semantic tokens arrive async from the server
and **refine/override** on top, snapping identifiers to their true type/modifier.

> **Consequence:** semantic highlighting is an **LSP feature**, not a theme/grammar feature. The
> theme only supplies the *colors*; the *classification* comes from the language server. This is why
> theming and LSP are one project, not two.

## 4. Two layers: color theme vs typography (fonts)

By strong convention (VS Code, Sublime, Vim, terminals), **fonts are not part of a color theme.**
Weavie follows this:

- **Color theme** = colors only (both tables above), spanning all surfaces (editor, terminal, chrome).
- **Typography** = independent settings: editor font family/size/weight/lineHeight/ligatures, and a
  (possibly distinct) terminal font. Per-OS defaults like VS Code.
- Optionally, a later **"appearance preset"** may *reference* one color theme + one typography set
  for one-click looks — but as a convenience bundle, never the source of truth.

**Known bug to fix when this lands:** `editor/monaco-setup.ts` currently hardcodes a macOS-only stack
(`ui-monospace, "SF Mono", Menlo, monospace`) which silently falls back to generic `monospace`
(Consolas) on Windows. Either drop the override (let Monaco pick per-OS defaults, like VS Code) or set
an intentional cross-platform stack.

## 5. The color vocabulary — adopt VS Code's keys as our own

Do **not** invent a parallel naming scheme. Use VS Code's workbench color keys
(`editor.background`, `terminal.ansiRed`, `editorGroupHeader.tabsBackground`, …) as Weavie's own
semantic token names. Then "reuse a VS Code theme" — and "override a color" — is nearly free across
all three rendering systems:

```
VS Code theme JSON
  ├─ colors{}            → Monaco theme colors          (1:1, same keys)
  │                      → xterm.js ITheme               (terminal.* → 1:1)
  │                      → CSS custom properties (:root) → chrome reads var(--editor-background) etc.
  ├─ tokenColors[]       → TextMate tokenization → syntactic colors
  └─ semanticTokenColors → LSP semantic tokens → semantic colors (overrides)
       (uiTheme → Monaco base: vs / vs-dark / hc-*)
```

Chrome CSS consumes `var(...)` named after VS Code keys, with fallbacks for keys a theme omits (many
themes skip `terminal.*` — derive from `editor.*` or a default ANSI palette).

## 6. Theme overrides (user tweaks, Claude-driven)

**Goal:** the user can tweak the active theme conversationally through Claude inside the editor —
"make the background pure black", "make everything 20% darker", "brighten the comments" — exposed via
MCP like the settings capability.

**Cheap by construction.** We already (a) use one color vocabulary (§5), (b) resolve→compile→apply
themes as a pure function with live re-application, and (c) have the MCP capability pattern from
settings. Overrides are just an extra input layer. Direct precedent: this *is* VS Code's
`workbench.colorCustomizations`, `editor.tokenColorCustomizations`, and
`editor.semanticTokenColorCustomizations` — sparse user patches over the active theme, keyed by the
same selectors. We mirror that model (and may mirror its JSON shape).

### Model — an ordered override stack
Overrides are a **sparse, ordered list** of declarative ops applied on top of the base theme at
resolve time. Two op kinds:

- **set** — a direct key override:
  `{ kind: "set", table: "colors"|"tokenColors"|"semanticTokenColors", key, value }`
  e.g. `{ set, colors, "editor.background", "#000000" }` → "background pure black". (last-write-wins per key)
- **transform** — a parametric op over a group of keys:
  `{ kind: "transform", op: "darken"|"lighten"|"saturate"|"desaturate"|"contrast", amount, target }`
  e.g. `{ transform, darken, 0.20, "all" }` → "everything 20% darker".

Applied **in order**, so "darken all, then set bg pure black" leaves bg pure black. Ordered +
declarative ⇒ trivial **undo** (pop the last op), **inspect** (list ops), and **survival across
base-theme switches** (transforms re-derive; sets re-apply by key).

### Two scopes
- **Global overrides** — apply regardless of active theme ("always pure-black bg").
- **Theme-scoped overrides** — keyed by theme id ("I tweaked Dracula"). Default for new tweaks.

Resolution: `base → global ops → theme-scoped ops → compile → apply live`.

### Composition pipeline (extends §5)
```
base theme ──► apply(global ops) ──► apply(theme-scoped ops) ──► effective theme
                                                                     │
                              ┌──────────────────────────────────────┤
                              ▼                  ▼                    ▼
                       Monaco defineTheme   xterm ITheme       CSS :root vars
                          + setTheme         (reassign)         (update)
```
All three re-apply **live** (no reload) on every override change — Monaco `setTheme`, xterm theme
reassign, and CSS var update are all cheap.

### Why a `transform` primitive matters (not just `set`)
For "make everything 20% darker", a direct-key-only model would force Claude to read ~hundreds of
colors and emit a giant darkened patch — token-heavy and error-prone. A `transform` op lets Claude
express the *intent* in one call; the resolver does the color math. So palette-wide asks →
`transform`; specific asks ("bg pure black", "comments = #888") → `set`. The two example asks map
exactly to the two op kinds.

**Color math:** transforms operate in **OKLCH** for perceptually-even results (HSL lightness scaling
is an acceptable v1). **Preserve alpha** — many VS Code colors are 8-digit hex (overlay alpha);
transform only the color component.

### MCP surface (mirrors the settings capability)
Registered in Core, surfaced as IDE-MCP tools (names illustrative):
- `theme.describe` — return active base theme + current effective palette + the override stack, so
  Claude can *see what it's working with* before tweaking.
- `theme.setOverride` — add a `set` op (one or many keys), `scope: global|theme`.
- `theme.applyTransform` — add a `transform` op.
- `theme.removeOverride` / `theme.undoLast` / `theme.reset` — remove one / pop last / clear a scope.
- (Theme selection — `theme.list` / `theme.select` — belongs to the broader theme capability.)

### Storage / ownership boundary
Override **values are user settings** — exactly how VS Code models `*Customizations`. So their
*persistence* belongs to the **settings system (separate agent)**, like the selected-theme setting.
The **theming subsystem owns**: the override schema, the resolver/composition, the color-math, the
live application, and the MCP tools. Clean split; no overlap with the settings agent's storage work.

## 7. Rendering stack decision

**Leading choice: `@codingame/monaco-vscode-api` in services/editor mode**, importing **only** the
`theme`, `textmate`, and `languages` service overrides.

Rationale:
- It implements the **full** VS Code coloring pipeline coherently: TextMate `tokenColors`, semantic
  `semanticTokenColors`, the semantic-tokens provider wiring, and the legend mapping. Since semantic
  highlighting is a hard requirement, this matters.
- The maintained `monaco-languageclient` (our LSP client) is **already built on** `monaco-vscode-api`
  — so we're in this ecosystem for LSP regardless. Reusing its theme/textmate overrides avoids
  running a *second* tokenizer.
- **Layout safety:** layout/window control lives only in the **workbench/views** service and the
  workbench init entry point. Using the services/editor init + `monaco.editor.create()` into our own
  DOM renders **zero** chrome. The hard-no is honored by *not importing* those packages.

Alternative considered — **Shiki (`@shikijs/monaco`)**: lighter, faithful TextMate themes, but
highlight-only. It does **not** give the semantic-token theming or the LSP substrate, and pairing it
with `monaco-languageclient` would mean two tokenization stacks. Rejected *given the semantic
highlighting requirement*; would be the pick if we only wanted colors.

**Open decision (see §16):** accept `monaco-vscode-api`'s bundle weight + build integration in
exchange for faithful semantic theming + LSP substrate. Leaning yes.

## 8. Themes & grammars — acquire from Open VSX `.vsix`

A VS Code theme is contributed by an **extension** (`.vsix` = a ZIP). Both themes and TextMate
grammars are **declarative data** — they register with **no extension host and no extension JS**.

**Acquisition (host-side):** Open VSX has a REST API:
- search: `GET https://open-vsx.org/api/-/search?query=…&category=Themes`
- metadata: `GET https://open-vsx.org/api/{namespace}/{name}` → versions + `files.download`
- download the `.vsix`, unzip, read `package.json` → `contributes.themes[]` (and
  `contributes.grammars[]` / `languages[]`), resolve each theme JSON (handle `include` chains).

Do fetch + unzip + storage in the **C# host** (it owns the user-data folder, `LocalFileSystem`, and
network; the WebView shouldn't). This also aligns with the **Claude-facing capability registry** —
"install/list/select theme" can later be registered commands surfaced over IDE-MCP.

> Grammars come from `.vsix` (TextMate). LSP **servers do not** (see §9). Don't conflate them:
> *grammar source ≠ server source*.

## 9. LSP — the Zed/Neovim model (servers from recipes, not extensions)

VS Code's "install a language extension" fuses two jobs behind one button — **acquire the server**
and **launch+connect it** — and does both via the **extension host** running activation JS. We
rejected that host. Zed and Neovim (and Helix) instead use a **native client + per-language recipe +
host-spawned subprocess over stdio**. We adopt that:

- **Client:** `monaco-languageclient` in the WebView (the web-side equivalent of `vim.lsp` / Zed's
  Rust client). Language-agnostic: once a server is launched and piped, it connects identically.
- **Per-language = a recipe, not code:** where to get the server, how to launch it (`cmd` + args),
  which file types trigger it, root markers. Crib recipe data from **`nvim-lspconfig`** (launch /
  filetypes / roots) and the **Mason registry** (download sources).
- **Server management (C# host):** a `LanguageServerAdapter` per supported language, shaped like
  Zed's `LspAdapter`:

  ```
  LanguageServerAdapter
    Detect()         → on PATH / known install dirs            (Neovim "bring-your-own")
    Fetch()/Update() → download from github-release/npm/…      (Zed-style, optional, toggle)
    LaunchCommand()  → cmd + args
    metadata         → file extensions, language ids, root markers, default settings/initOptions
  ```

  **Resolution order per language:** user-set path → on `PATH` → auto-download (if enabled) → skip.
  (= Zed auto-manage + Neovim bring-your-own, in one.)

- **TS/JS note:** Monaco's bundled TypeScript worker already gives decent TS/JS IntelliSense, so LSP
  is *additive* for non-TS languages. (We may later replace it with `typescript-language-server`/
  `vtsls` for consistency, but it's not required.)

## 10. Process topology

```
┌──────────────────────────────────────────────────────────────────────────┐
│ WebView2 renderer  (SolidJS app)                                           │
│    Monaco editor ──edits / hovers / cursor──► monaco-languageclient        │
│         ▲                                              │                   │
│         │ squiggles, hovers, completions,              │ LSP JSON-RPC      │
│         │ go-to-def, SEMANTIC COLORS                    │ as WS frames      │
└─────────┼──────────────────────────────────────────────┼──────────────────┘
          │                                               │ ws://127.0.0.1:PORT
          ▼                                               ▼
┌──────────────────────────────────────────────────────────────────────────┐
│ C# host (Weavie.Win)              LSP BRIDGE  (WebSocket ◄──► stdio)        │
│   LanguageServerManager                │            │                      │
│     · resolve binary (PATH / download) │            │  Content-Length–     │
│     · spawn + supervise + restart      │            │  framed JSON-RPC on  │
│     · one bridge endpoint per server   │            │  stdin/stdout        │
└─────────────────────────────────────────┼────────────┼─────────────────────┘
                                          │            │
                                ┌─────────▼────┐  ┌────▼─────────┐
                                │ rust-analyzer│  │ pyright      │  … one per language
                                │ (subprocess) │  │ (subprocess) │
                                └──────────────┘  └──────────────┘
```

Three transport hops, all LSP JSON-RPC, framed differently:
- **Monaco ↔ client:** in-process JS.
- **client ↔ host:** loopback **WebSocket** (same 127.0.0.1 pattern as the IDE-MCP server).
- **host ↔ server:** host is a dumb proxy — WS frame ↔ one `Content-Length`-framed message on the
  server's stdio. (= the `vscode-ws-jsonrpc` proxy from monaco-languageclient examples.) Spawning +
  piping reuses the host's existing ConPTY muscle.

## 11. Sequence flow

```
User      Monaco        monaco-langclient      Host (mgr+bridge)        Server (e.g. pyright)
 │ open .py   │                 │                      │                        │
 │───────────►│ set model+lang  │                      │                        │
 │            │────────────────►│ ensure server(py) ──►│ spawn if not running ─►│ (process starts)
 │            │                 │   open WS  ◄────────► │  (bridge attached)     │
 │            │                 │ initialize ──────────┼───────────────────────►│
 │            │                 │◄ initializeResult ───┼────────────────────────│  ← capabilities +
 │            │                 │   (caps + semantic   │                        │    semanticTokens
 │            │                 │    LEGEND)           │                        │    LEGEND
 │            │                 │ initialized ─────────┼───────────────────────►│
 │            │                 │ didOpen(full text) ──┼───────────────────────►│
 │            │◄ squiggles ──────│◄ publishDiagnostics ┼────────────────────────│  (server PUSHES)
 │            │                 │ semanticTokens/full ─┼───────────────────────►│
 │            │◄ recolor ────────│◄ token ints ────────┼────────────────────────│
 │ type 'x'   │                 │                      │                        │
 │───────────►│ change event    │                      │                        │
 │            │────────────────►│ didChange(delta) ────┼───────────────────────►│  (debounced)
 │            │◄ squiggles ──────│◄ publishDiagnostics ┼────────────────────────│
 │            │                 │ semanticTokens/delta ┼───────────────────────►│  (just the diff)
 │            │◄ recolor ────────│◄ token delta ───────┼────────────────────────│
 │ hover      │                 │                      │                        │
 │───────────►│────────────────►│ hover request ──────┼───────────────────────►│  (on demand)
 │            │◄ hover widget ───│◄ hover result ──────┼────────────────────────│
 │ close      │                 │ didClose/shutdown ───┼───────────────────────►│ exit → host kills proc
```

Triggering rules:
- **Connection** is triggered by opening a file whose language has an adapter — lazy, per workspace
  root (host already resolves the workspace).
- **Diagnostics** are **server-pushed** (`publishDiagnostics`) after open/change — never requested.
- **Hover / completion / definition / rename** are **pull / on-demand**, triggered by cursor/keys.
- **Semantic tokens** are pull + refresh: requested for the doc, `/delta` on change, and the server
  can fire `workspace/semanticTokens/refresh` to ask the client to re-pull.

## 12. Scoped vs semantic colors (explainer)

**Scoped (TextMate) colors** — the syntactic layer:
```
// comment   → comment.line.double-slash.ts
const        → storage.type / keyword
greet        → entity.name.function.ts      (it sits before "(")
name         → variable.parameter / variable.other.readwrite.ts
"hi"         → string.quoted.double.ts
```
A theme's `tokenColors` maps **dotted scope selectors** → colors with CSS-like specificity (more dots
= more specific). The grammar doesn't understand the code — it pattern-matches punctuation — so class
vs namespace vs local all look like bare identifiers. That's the ceiling of scope coloring.

**Semantic colors** — `textDocument/semanticTokens`, from the server that ran the compiler front-end:
- **token types:** `namespace, class, enum, interface, struct, typeParameter, type, parameter,
  variable, property, enumMember, function, method, macro, keyword, …`
- **token modifiers:** `declaration, readonly, static, abstract, async, deprecated, defaultLibrary, …`
- on the wire: a compact int array, **5 ints/token** `[ΔLine, ΔStartChar, length, typeIndex,
  modifierBitset]`, decoded against the **legend** the server announced at `initialize`.

This is exactly Visual Studio's classifier: color by what the compiler *knows it is*. It **overrides**
the TextMate baseline once it arrives.

## 13. Theme storage layout

Store the **raw VS Code theme JSON + a small manifest; convert at load.** The VS Code format is the
portable, lossless source of truth; the converter (scopes→Monaco via the textmate service,
colors→xterm/CSS) is a pure function applied at load. If we later improve the converter, every stored
theme improves with no re-install. (User **overrides** are *not* stored here — see §6: they're user
settings, owned by the settings agent.)

```
%LOCALAPPDATA%/weavie/themes/
  index.json                         # [{ id, label, type, uiTheme, source:{registry,namespace,name,version}, path }]
  <namespace>.<name>-<version>/
    themes/<theme>.json              # raw VS Code theme JSON(s)
  (built-in themes bundled read-only with the app, merged into the same logical list)
```

- **Install unit = extension; selection unit = individual theme** (one extension can contribute
  several, e.g. Dark+/Light+). `index.json` enumerates individual themes, each pointing back to its
  source extension/version.
- Bundle 1–2 built-in defaults (read-only) so there's always something before any install.
- Licensing: Open VSX redistribution is its purpose; *user-initiated* install is the user's act. Only
  audit per-extension licenses if we ever *bundle* third-party themes by default.

## 14. Build / integration considerations

- **Assets:** the textmate/theme stack ships **oniguruma WASM** + grammars; servers/grammars are
  files that must be served. Under the `https://weavie.app/` virtual-host mapping this is doable but
  is real wiring (workers + asset paths).
- **Workers:** Monaco + the language services use web workers; ensure they load under the custom
  scheme (we already build Monaco workers as classic/iife for this reason).
- **Discipline:** `monaco-vscode-api` docs lean toward full-workbench examples — we must consciously
  stay in the minimal slice (see §17).
- **Version coupling:** `monaco-vscode-api` tracks specific VS Code versions; upgrades are real work.

## 15. Implementation phases (ladder)

1. **Theme spike** — load one Open VSX `.vsix` theme into the current Monaco (colors + tokenColors).
   Prove the slice without the full stack.
2. **Stack adoption** — bring in `monaco-vscode-api` (theme + textmate overrides, services/editor
   mode), wire colors → Monaco + xterm + CSS vars. Built-in default theme. Establish the
   resolve→compile→apply pure function.
3. **Theme install pipeline** — host-side Open VSX search/download/unzip/register + storage (§13).
4. **Overrides — `set` ops** — direct key overrides (global + theme-scoped), live apply, MCP
   `theme.describe` / `theme.setOverride` / `theme.removeOverride` / `theme.reset`. Trivial once the
   resolve→apply function exists (§6).
5. **LSP v0 (bring-your-own)** — `languages` override + `monaco-languageclient` over loopback
   WebSocket; host detects servers on `PATH`; one or two languages. Diagnostics/hover/completion.
6. **Semantic highlighting** — semantic-tokens provider + `semanticTokenColors` honored end-to-end.
7. **Overrides — `transform` ops** — OKLCH color math + `theme.applyTransform` / `theme.undoLast`.
8. **LSP v1 (managed servers)** — `LanguageServerAdapter` registry with download recipes
   (nvim-lspconfig / Mason data); resolution order; supervision/restart.
9. **(Optional) registry breadth** — consume Mason registry data for more languages.

## 16. Open decisions

- **D1 — Rendering stack:** accept `monaco-vscode-api` (theme+textmate+languages) for faithful
  semantic theming + LSP substrate, vs. lighter Shiki-only (no semantic theming). **Leaning
  monaco-vscode-api** because semantic highlighting is required. *Needs sign-off (bundle weight).*
- **D2 — Server install posture for v1:** ship bring-your-own only, or add Zed-style auto-download?
  (v0 is bring-your-own regardless.)
- **D3 — First languages** to support (suggest: TS/JS via Monaco worker already; then Python via
  pyright, Rust via rust-analyzer).
- **D4 — Font stack** resolution (drop override for per-OS defaults vs intentional cross-platform
  stack) — tracked with the typography layer.
- **D5 — Overrides transform model:** ordered declarative ops (re-derivable across theme switches,
  recommended) vs materialized patches; and color space for transforms (OKLCH vs HSL v1).

## 17. Guardrails — "do-not-import" list

To keep the hard-no (no layout/workbench, no config/keybindings/commands, no extension host):

- ❌ Any `@codingame/monaco-vscode-*views*` / workbench / layout service override or workbench init.
- ❌ `*-configuration-service-override` (file-backed settings + UI). Drive config in-memory,
  programmatically, from Weavie's own settings.
- ❌ `*-keybindings-service-override`, command/quick-access overrides.
- ❌ Extension **host** / running extension activation JS. Only **declarative** `.vsix` contributions
  (themes, grammars, language configs) via `registerExtension`-style data registration.
- ✅ Allowed: `theme`, `textmate`, `languages` service overrides; `monaco-languageclient`; editor
  created via `monaco.editor.create()` into Weavie's own DOM.

## 18. References

- VS Code color theme format (`colors`, `tokenColors`, `semanticTokenColors`, `semanticTokenScopes`)
  and customizations (`workbench.colorCustomizations`, `editor.tokenColorCustomizations`,
  `editor.semanticTokenColorCustomizations`).
- LSP spec: `initialize`, `textDocument/didOpen|didChange`, `publishDiagnostics`,
  `textDocument/hover|completion|definition|rename`, `textDocument/semanticTokens(/full|/range|/delta)`.
- `@codingame/monaco-vscode-api` (service overrides; services vs workbench init).
- `monaco-languageclient` + `vscode-ws-jsonrpc` (WebSocket ↔ stdio proxy).
- Open VSX REST API.
- `nvim-lspconfig` (launch recipes), `mason.nvim` / `mason-registry` (install recipes), Zed
  `LspAdapter` (managed server pattern).
- OKLCH / perceptual color spaces (for override transforms).
```
