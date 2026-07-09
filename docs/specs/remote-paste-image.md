# Remote image paste into agents

Status: **built, verified by tests, the real `claude` TUI, and live Codex app-server `localImage`
acceptance.** Covers browser-served DOM paste capture, native-WebView Ctrl/Cmd+V read, Claude PTY
delivery, and structured-provider delivery.

Pasting an image into an agent works when the backend is remote/headless and the provider cannot reach the
desktop clipboard. The image is captured in the browser (or, on a native WebView, read from the OS clipboard
through the local host), shipped to the backend, written to a scratch file there, and delivered through the
provider's native image path: Claude receives a bracketed-pasted path in its PTY, while Codex attaches the
path and includes it as a `localImage` app-server input item with the next prompt.

## Why Claude uses path-injection

Claude Code ingests an image exactly two ways: the **OS clipboard** (native APIs — unavailable on the
headless/remote Linux backend, which has no clipboard, and where Claude's Linux clipboard-image support
doesn't exist) or a **file path in the prompt**. There is no PTY/escape-sequence route for image *bytes*, and
the IDE-integration channel that pushes `selection_changed` ("⧉ N lines selected") is text/selection-only — it
carries no image message, and the protocol is Claude-defined, so Weavie can't invent one. Empirically:

- A **bare path in the prompt is natively multimodally attached** (`claude -p … --allowedTools ""` read back a
  hand-built PNG's pixel colors — no tool read possible).
- Injecting that path **as a bracketed paste** (`ESC[200~<path>ESC[201~`) makes the TUI render a clean
  `[Image #N]` chip; the raw path never shows. Bare path, not `@`-prefixed.

So temp-file-plus-path isn't a workaround — it's what Anthropic's own IDE integration and every community
extension do. Codex uses the same scratch-file boundary, then attaches that path to the next app-server
`localImage` input instead of PTY paste.

## Flow

```mermaid
flowchart LR
  subgraph web [WebView / browser tab]
    paste[paste event on agent input] -->|image item?| enc[read blob -> bytesToBase64]
    paste -->|text only| xterm[xterm native text paste]
    enc -->|size/mime pre-check, else notify| post["postToHost term-paste-image"]
  end
  post --> disp["HostCore.Dispatch"]
  disp --> h["HandlePasteImage"]
  h -->|validate mime + size, else Notify| store["session.PastedImages.Write -> path"]
  h -->|!_drainInputFrozen| deliver["session.SendAgentImagePath(path)"]
  deliver --> claude["Claude: WriteBracketedPaste(path)"]
  deliver --> codex["Codex: attach path for next localImage input"]
  claude --> pty[(claude PTY)] --> chip["TUI: [Image #N], attaches on submit"]
```

- **Web** (`src/web/src/terminal/paste-image.ts`, wired in `TerminalView.tsx` and `AgentPane.tsx`): an image
  paste listener consumes image items only (`preventDefault` + `stopImmediatePropagation`); text pastes fall
  through untouched. Pre-checks size (mirrors the host cap) so oversize bytes never ride the bridge, then posts
  `term-paste-image { slot, session, mime, dataB64 }` to the backend that owns the session slot.
- **Host** (`HostCore.PasteImage.cs`): drain-guard → validate MIME→extension + decoded size (reject with a
  `Notify`) → `PastedImageStore.Write` (host picks the filename; the client never supplies a path) →
  `HostSession.SendAgentImagePath(path)`. Claude gets a bracketed paste immediately; Codex stores it as a
  pending image attachment for the next prompt.
- **Storage** (`PastedImageStore`, `WeaviePaths.WorkspacePastedImagesDir`): a per-session subdir keyed by
  worktree digest (`~/.weavie/workspaces/<id>/pasted-images/<digest>/paste-N.<ext>`), outside the workspace so
  it never reaches the tree/index/git. Wiped on session unload (`HostSession.DisposeAsync`).

## Decisions

- **Event, not a command.** Driven by the native paste gesture carrying image bytes (only reachable inside the
  event); no palette action to advertise. Mirrors how browser-served **text** paste already rides the native
  paste event rather than `weavie.terminal.paste`.
- **Agent panes only.** A pasted path in a shell would just try to run; the host targets the session's agent
  regardless of the message's `session`.
- **Allowlist + cap are one source of truth** (`PastedImageMedia`): png/jpeg/gif/webp, `MaxBytes` = 5 MB
  (Claude's per-image limit). The web mirrors the values; the **host is the authoritative gate**. A rejected
  paste (bad type / oversize) is surfaced as a toast — never a silent drop.
- **Cleanup on unload only.** `PastedImages.Clear()` in `DisposeAsync` covers the normal path. No blanket
  boot-time wipe: it would delete a concurrent same-workspace instance's live images (a risk scratch's
  keep-set GC deliberately avoids), and a hard-crash orphan is a few KB in a hidden dir.

## Native WebView

On a **browser-served** shell `weavie.terminal.paste` declines, the Ctrl/Cmd+V keydown falls through, and the
DOM `paste` event fires — the browser capture handles it. On a **native WebView** that command consumes
Ctrl/Cmd+V and `preventDefault`s it, so the DOM paste event never fires. A native desktop running Claude
*locally* can still ingest a clipboard image via Claude's own OS-clipboard support — but a native app driving a
**remote/headless** session cannot: Claude runs on the backend with no access to the desktop clipboard, so the
paste silently did nothing. That is the common way the native app hits a remote session, so it is the bug, not
an edge case.

So the native paste command reads the OS clipboard itself. On the claude pane it asks the **local** host — which
owns the clipboard even when a remote backend drives the page — for a clipboard *image* (`clipboard-read-image`
→ `IHostPlatform.ReadClipboardImage`, decoded to PNG per OS: `NSPasteboard` / `Clipboard.GetImage` / gdk-pixbuf;
headless has none and returns `ClipboardImage.None`). A present image rides to the **active** backend as the same
`term-paste-image` message the browser capture posts (scratch file + path injection); no image falls through to a
text paste, unchanged.

## Tests

- `HostCorePasteImageTests` (headless, at the PTY seam): a pasted PNG writes a scratch file with the right
  bytes/extension and injects `ESC[200~<path>ESC[201~` into the claude PTY; each allowed MIME maps to its
  extension; a disallowed type / oversize paste is toasted and never written; a shell-named paste never
  reaches the shell; a paste is suppressed while input is frozen for an update. Plus the native-WebView read:
  `clipboard-read-image` replies with the platform's clipboard image (bytes + MIME), or an empty MIME for none.
- `HostSessionAgentImageTests` (host session seam): structured providers receive pasted images through
  `AttachImage(path)`, not prompt text.
- `CodexAppServerProtocolTests` / `CodexAppServerSessionTests`: Codex image attachment is serialized with
  the next prompt as app-server `localImage` input.
- `paste-image.test.ts` (web): the shared DOM paste helper posts image paste messages and leaves text-only
  paste untouched.
- `PastedImageStoreTests` (Core): sequential `paste-N` allocation, byte-exact writes, `Clear` on unload, and
  the `PastedImageMedia` allowlist.
