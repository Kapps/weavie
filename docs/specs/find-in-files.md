# Find in Files

Project-wide content search: a left-docked panel (`Ctrl/⌘+Shift+F`) over `git grep` on the active
session's worktree. Seeded from the editor selection, steered entirely from the keyboard, filterable by
include/exclude globs, with match case / whole word / regex options.

## Interaction model

- **Seed**: invoking Find in Files captures the editor's single-line selection (if any) as the query and
  searches immediately; the input is focused with its text selected, so typing replaces it. Re-invoking
  while open re-seeds/refocuses. No selection → the previous query is kept (module store, below).
- **Preview vs commit**: `↑/↓` move the selection and *live-preview* the match — the editor reveals
  line:column in the reusable preview tab **without taking focus** (debounced ~120 ms so holding the key
  doesn't open every file it passes). `Enter` (or click) commits: same open, but focus moves to the editor.
  The panel stays open; `Esc` closes it and returns focus to the editor.
- **Result stepping**: `F4` / `Shift+F4` jump to the next/previous result from anywhere (no `when` gate;
  the handlers decline when there are no results so the keys fall through). Results persist in a
  module-level store, so stepping works after the panel is closed and reopening is instant.
- **Options**: match case (`Alt+C`), whole word (`Alt+W`), regex (`Alt+R`), and the include/exclude filter
  row (`Ctrl/⌘+Shift+J`) are catalog commands gated `when: searchPanelFocused` (context set on the panel's
  focusin/focusout); every button's tooltip advertises its effective binding via `keyHint`. Toggling
  re-searches immediately. The filter row stays visible while a glob is set, so an active filter is never
  hidden state.
- **Feedback**: a summary strip ("N matches in M files", or the truncation warning at the 500-match cap),
  match substrings highlighted in each row, sticky file-group headers with match counts, a loud error strip
  for a failed grep (e.g. an invalid regex — git's own message, never "No results"), and a hints footer.

## Protocol

Web → host: `{ type: "find-in-files", token, query, caseSensitive, wholeWord, regex, include, exclude }`.
Host → web: `{ type: "find-in-files-results", token, query, matches, truncated, error? }` where each match
is `{ path, line, column, preview }` (absolute canonical path; 1-based UTF-16 column of the line's first
match). `token` is a monotonic request id echoed back — the sole stale-drop key, since options changes
re-search under the same query text. An empty query clears results without running git.

## git grep mapping (`GitGrep`, pure)

```
git grep -n --column -z -I --no-color --untracked (-F|-E) [-i] [-w] -e <query> -- <pathspecs…>
```

- `-F` literal by default; `regex` swaps to POSIX ERE `-E`. `-i` unless `caseSensitive`; `-w` for
  `wholeWord`. `-e <query> --` keeps an option-shaped query an operand.
- `-z --column` yields `path NUL line NUL column NUL text` records; `GitGrep.Parse` is exact for paths
  containing `:` and converts git's byte column to UTF-16 (`Utf16Column`) for JS/Monaco.
- Include/exclude are comma-separated globs expanded per token (VS Code semantics), excludes with
  `:(exclude,glob)` magic:

| token | pathspec(s) |
| --- | --- |
| bare name (`*.ts`, `node_modules`) | `:(glob)**/tok` + `:(glob)**/tok/**` (any depth, file or dir) |
| contains `/` (`src/**`, `docs/a.md`) | `:(glob)tok` (root-anchored; leading `./` and `/` stripped) |
| trailing `/` (`docs/`) | token + `**`, then the rules above |

- Results cap at `GitGrep.MatchCap` (500) with `truncated` surfaced in the panel. A non-0/1 exit throws
  `GitException` carrying git's stderr, which rides back as `error`.

## Structure

- `Weavie.Core/Git/GitGrep.cs` — pure argv/pathspec/parse layer (unit-tested without git);
  `GitService.GrepAsync` composes it with `RunAsync`. `GrepOptions`/`GrepMatch` in `GrepMatch.cs`.
- `Weavie.Hosting/HostCore.Search.cs` — the host seam: parses the message, runs the grep off the UI
  thread, guards the reply against a session switch.
- `src/web/src/chrome/search-model.ts` — pure grouping / visible-row navigation / highlight positions
  (literal replays git's `-F/-i/-w` semantics; regex approximates ERE with a JS RegExp, degrading to
  no-highlight for untranslatable patterns — cosmetic only, the result list stays authoritative).
- `src/web/src/chrome/search-store.ts` — module-level state + host messaging (results survive unmount;
  the opener is injected by App from the editor controller).
- `src/web/src/chrome/SearchPanel.tsx` — the view. Opens results through
  `EditorController.openMatch` → `openTab` placement `{ line, column, focus }` (no host round-trip);
  `editor-host` skips `editor.focus()` when `focus === false`.

## Tests

- `tests/Weavie.Core.Tests/Git/GitGrepTests.cs` — argv matrix, pathspec expansion, NUL parser, byte→UTF-16.
- `src/web/src/chrome/search-model.test.ts` — grouping, collapsed-group navigation, highlight positions.
- `src/web/e2e/functional/find-in-files.spec.ts` — full-stack journeys on the real headless host: seeding,
  preview/commit/F4 focus semantics, all option chords, globs, and the bad-regex error strip.
