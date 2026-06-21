import {
  For,
  type JSX,
  Show,
  Suspense,
  createEffect,
  createMemo,
  createSignal,
  lazy,
  onCleanup,
  onMount,
} from "solid-js";
import {
  type SessionStatusName,
  type TermSession,
  activeBackendId,
  onHostMessage,
  postToBackend,
  postToHost,
  setActiveBackendId,
} from "./bridge";
import { DeleteSessionDialog, type DeleteSessionState } from "./chrome/DeleteSessionDialog";
import { MacTitleBar } from "./chrome/MacTitleBar";
import { NewSessionPrompt } from "./chrome/NewSessionPrompt";
import { RegisterAgentModal } from "./chrome/RegisterAgentModal";
import { ResizeFrame } from "./chrome/ResizeFrame";
import { SessionRail } from "./chrome/SessionRail";
import { TitleBar } from "./chrome/TitleBar";
import { focusOmnibar } from "./chrome/omnibar-controller";
import { connectStoredAgents } from "./chrome/remote-agents";
// Named imports keep the session store loaded at top level (out of any hot-swapping component) so the
// rail + active-session status survive HMR, like layout/store and editor/session-store.
import { type RailSession, claudeStatus, sessions } from "./chrome/session-store";
import { setContext } from "./commands/context";
import { installDoubleShift } from "./commands/double-shift";
import { formatKey, installKeybindings } from "./commands/keybindings";
import { dispatchCommand, getKeybindings, registerCommand } from "./commands/registry";
import { CommandIds } from "./commands/types";
import { debounce } from "./debounce";
import { ConfirmDialog } from "./editor/ConfirmDialog";
import { EditorEmptyState } from "./editor/EditorEmptyState";
import { TabStrip } from "./editor/TabStrip";
import { createEditorController } from "./editor/editor-controller";
// Registers the editor session store's set-editor-session listener at top-level module load — before
// main.tsx posts "ready" and the host replies with its one-shot restore push. The store otherwise lives
// only in the dynamically-imported editor chunk (editor-host), which loads later, so the push would arrive
// with no listener — launch/Ctrl+R restore would silently no-op. Importing here also keeps it alive across HMR.
import { activePath, flushEditorSession, openTabs } from "./editor/session-store";
import type { DirListings } from "./files/FileBrowser";
import { LayoutView } from "./layout/LayoutView";
import { paneOrder } from "./layout/geometry";
import { DEFAULT_LAYOUT_ROOT, layoutDocument, sendLayout } from "./layout/store";
import type { LayoutNode } from "./layout/types";
import { rebindLanguageServices } from "./lsp/lsp-client";
import { Toasts, createToasts } from "./notify/Toasts";
import { mark } from "./startup-timing";
import { TerminalView } from "./terminal/TerminalView";
import { applyChromeTheme } from "./theme";

const FileBrowser = lazy(() => import("./files/FileBrowser"));

// The PRIMARY session's workspace root (host-injected before navigation). Seeds the active-root signal
// (indexRoot) and serves as the "is there a host workspace at all" check; the live root follows the active
// session via the host's file-index pushes. Null in plain-browser dev (no host).
const WORKSPACE_ROOT = window.__WEAVIE_LSP__?.workspace ?? null;

// Host-injected shell config. Windows injects titleBar "custom" (the frameless web title bar with its own
// window controls); macOS injects titleBar "mac" (the omnibar strip below the native title bar + system
// menu). Absent in plain-browser dev, where neither bar renders and the floating Files button is the toggle.
const SHELL = window.__WEAVIE_SHELL__;
const CUSTOM_TITLEBAR = SHELL?.titleBar === "custom";
const MAC_TITLEBAR = SHELL?.titleBar === "mac";
// Either title-bar mode renders the omnibar + view toggles, so the floating panel buttons aren't needed.
const HAS_TITLEBAR = CUSTOM_TITLEBAR || MAC_TITLEBAR;

// Human-readable tooltip for each Claude status, shown on the pane-head status dot.
const STATUS_LABEL: Record<SessionStatusName, string> = {
  starting: "Claude is starting",
  working: "Claude is working",
  needsInput: "Claude needs your input",
  idle: "Claude is idle",
  error: "Claude crashed",
};

// Maps a terminal pane kind ("terminal:claude" / "terminal:shell") to its pane id.
const paneOf = (kind: string): TermSession => (kind === "terminal:claude" ? "claude" : "shell");

export default function App(): JSX.Element {
  let editorContainer!: HTMLDivElement;
  // The live pane layout tree: seeded with the default, replaced when the host pushes the persisted
  // layout, and updated optimistically while the user drags a splitter.
  const [layoutRoot, setLayoutRoot] = createSignal<LayoutNode>(DEFAULT_LAYOUT_ROOT);
  // The pane that currently has keyboard focus (tracked from focusin), for the active highlight.
  const [focusedKind, setFocusedKind] = createSignal<string | null>(null);
  // Pane kinds in DFS order; index + 1 is the pane's Ctrl+N number.
  const paneNumbers = createMemo(() => paneOrder(layoutRoot()));
  const numberOf = (kind: string): number => paneNumbers().indexOf(kind) + 1;
  // The pane-switch badge, read from the resolved keybindings by matching the focus-pane binding whose
  // index arg is this pane's number (never hardcoded). Unbound → just the number.
  const paneShortcut = (kind: string): string => {
    const number = numberOf(kind);
    const match = getKeybindings().find(
      (binding) =>
        binding.command === CommandIds.focusPaneByIndex &&
        (binding.args as { index?: unknown } | undefined)?.index === number,
    );
    return match !== undefined ? formatKey(match.key) : `${number}`;
  };
  // Each loaded session's terminal panes register their focus fn here on mount, keyed by `${slot}:${pane}`
  // (the editor focuses via the controller directly). focusPane resolves the active session's entry.
  const terminalFocus = new Map<string, () => void>();

  // The active backend's loaded sessions each keep their own live xterm pair mounted; only the active one is
  // shown. A stable string[] of session ids so <For> never remounts a session's terminals across rail pushes
  // — that keeps them alive, making a switch pure show/hide. Dormant and other-backend sessions are excluded.
  const termSessionIds = createMemo(() =>
    sessions()
      .filter((s) => s.loaded && s.backendId === activeBackendId())
      .map((s) => s.id),
  );
  // The session whose panes are shown — the active one on the active backend (or null before the first
  // rail push). Flipping this is what switches which session's terminals are visible.
  const activeTermSessionId = createMemo(() => sessions().find((s) => s.active)?.id ?? null);

  // Whether the "New session" prompt (branch name + base) is open; the rail's "+" opens it. (Claude status
  // and the rail session list live in chrome/session-store as HMR-surviving top-level signals.)
  const [newSessionOpen, setNewSessionOpen] = createSignal(false);
  const [registerAgentOpen, setRegisterAgentOpen] = createSignal(false);
  const [dirListings, setDirListings] = createSignal<DirListings>({});
  const [browserOpen, setBrowserOpen] = createSignal(false);
  // The file currently shown in the editor, tracked so the browser can highlight + reveal it.
  const [currentFile, setCurrentFile] = createSignal<string | null>(null);
  // User-facing toasts (e.g. an autosave write that failed) — surfaced rather than silently dropped.
  const { toasts, addToast, dismissToast } = createToasts();
  // A pending "discard unsaved scratch?" confirm: the names to discard + the promise resolver the dialog
  // settles. The editor controller routes every tab close through this guard (confirmDiscard below).
  const [confirmReq, setConfirmReq] = createSignal<{
    names: string[];
    resolve: (ok: boolean) => void;
  } | null>(null);
  const confirmDiscard = (names: string[]): Promise<boolean> =>
    new Promise<boolean>((resolve) => setConfirmReq({ names, resolve }));
  const settleConfirm = (ok: boolean): void => {
    const req = confirmReq();
    if (req !== null) {
      setConfirmReq(null);
      req.resolve(ok);
    }
  };
  // Window chrome (maximize glyph + blur dim) pushed by the host, plus the flat workspace file index the
  // omnibar's "Go to File" and the file browser tree share. indexRoot is the ACTIVE session's worktree root
  // — it follows session switches (the host re-pushes file-index on each), seeded from WORKSPACE_ROOT until
  // the first push.
  const [maximized, setMaximized] = createSignal(false);
  const [windowFocused, setWindowFocused] = createSignal(true);
  const [fileIndex, setFileIndex] = createSignal<string[]>([]);
  const [indexRoot, setIndexRoot] = createSignal<string | null>(WORKSPACE_ROOT);

  // The Monaco editor + all diff/review orchestration; App feeds it host messages and commands.
  const editor = createEditorController({
    onSaveError: (message) => addToast("error", message),
    onCurrentFileChanged: setCurrentFile,
    confirmDiscard,
  });

  const focusPane = (kind: string): void => {
    if (kind === "editor") {
      editor.focusEditor();
      return;
    }
    // Every loaded session has its own xterm pair; only the active session's is visible/focusable. Resolve
    // it by the active session id, so focus lands correctly regardless of effect-flush timing on a switch.
    const pane = paneOf(kind);
    const sid = activeTermSessionId();
    if (sid !== null) {
      terminalFocus.get(`${sid}:${pane}`)?.();
    }
  };

  // Switch to a session by id. Flushes the outgoing session's pending (debounced) editor session before
  // the switch so its tab set isn't lost; the host processes both messages in order on the still-active
  // session. Used by the rail (click) and the next/prev keyboard commands alike.
  const switchToSession = (session: RailSession): void => {
    flushEditorSession();
    // Crossing to another backend (local ↔ remote) rebinds the page to it; its switch-session reply
    // (term-reset → the panes re-emit term-ready, plus set-editor-session) re-attaches the terminals + editor.
    if (session.backendId !== activeBackendId()) {
      setActiveBackendId(session.backendId);
    }
    postToBackend(session.backendId, { type: "switch-session", id: session.id });
  };

  // Step the active session to the next/prev LOADED chip on the rail (delta ±1, wraps around). Dormant chips
  // are deliberately skipped — a parked session shouldn't sit in the cycle; reach it by clicking or Switch
  // Session… instead. Returns whether it stepped, so with <2 loaded sessions (nothing to switch to) the
  // keystroke falls through, matching tab next/prev.
  const stepSession = (delta: number): boolean => {
    const list = sessions().filter((s) => s.loaded);
    const current = list.findIndex((s) => s.active);
    if (list.length < 2 || current < 0) {
      return false;
    }
    const target = list[(current + delta + list.length) % list.length];
    if (target === undefined) {
      return false;
    }
    switchToSession(target);
    return true;
  };

  // A pending session delete, opened only once the host classifies the worktree. The deletePrompt command sends
  // a delete-session-request; the host replies with session-delete-prompt carrying the worktree's state, which
  // opens DeleteSessionDialog with the matching confirm (clean / untracked / modified).
  const [deleteReq, setDeleteReq] = createSignal<{
    id: string;
    label: string;
    state: DeleteSessionState;
  } | null>(null);
  // The interactive delete (rail menu / palette): with no `id` arg it targets the active session. Asks the host
  // to classify the worktree; the session-delete-prompt reply opens the dialog.
  const promptDeleteSession = (args: unknown): void => {
    const id = (args as { id?: string } | undefined)?.id ?? sessions().find((s) => s.active)?.id;
    if (id !== undefined) {
      postToHost({ type: "delete-session-request", id });
    }
  };
  const confirmDeleteSession = (): void => {
    const req = deleteReq();
    if (req === null) {
      return;
    }
    setDeleteReq(null);
    // A dirty worktree (untracked or modified) needs force, or git refuses the removal.
    postToHost({ type: "delete-session", id: req.id, force: req.state !== "clean" });
  };

  // Persist the layout after a user gesture (debounced). Skipped until the host has pushed the initial
  // layout, so we never overwrite the saved state with the default before it has loaded.
  const flushLayout = debounce(sendLayout, 400);
  const persistRoot = (root: LayoutNode): void => {
    const base = layoutDocument();
    if (base === null) {
      return;
    }
    flushLayout({ ...base, root });
  };

  // A splitter drag: show the new sizes immediately, persist on a debounce.
  const onLayoutResize = (root: LayoutNode): void => {
    setLayoutRoot(root);
    persistRoot(root);
  };

  // Apply the layout the host pushes (startup restore + any later host/MCP change). The resize handler
  // is gesture-driven, so applying a pushed layout never echoes back into a save.
  createEffect(() => {
    const doc = layoutDocument();
    if (doc !== null) {
      setLayoutRoot(doc.root);
    }
  });

  // Renders the surface for a pane kind. Called once per kind by LayoutView (the slot list is stable), so
  // the editor surface and each terminal kind's container are created once and only repositioned. Within a
  // terminal kind, one xterm per loaded session is mounted (only the active shown) — see the For below.
  const renderPane = (kind: string): JSX.Element => {
    if (kind === "editor") {
      return (
        <div
          class="editor-surface"
          classList={{ active: focusedKind() === "editor" }}
          data-kind="editor"
        >
          <TabStrip tabs={openTabs} activePath={activePath} actions={editor.tabs} />
          <div class="editor-pane">
            <div class="editor" ref={editorContainer} />
            {/* No file open: cover the blank Monaco host with an identity + keyboard-first starter actions. */}
            <Show when={openTabs().length === 0}>
              <EditorEmptyState />
            </Show>
          </div>
          {/* Pane-switch badge: top-right of the PANE (over the tab strip), not over the editor content. */}
          <span class="pane-shortcut editor-badge">{paneShortcut("editor")}</span>
        </div>
      );
    }
    const pane = paneOf(kind);
    return (
      <div class="terminal-surface" classList={{ active: focusedKind() === kind }} data-kind={kind}>
        <div class="pane-head">
          <span class="pane-label">{kind === "terminal:claude" ? "Claude Code" : "Terminal"}</span>
          <Show when={kind === "terminal:claude" && claudeStatus() !== undefined}>
            <span
              class={`session-status status-${claudeStatus()}`}
              title={STATUS_LABEL[claudeStatus() as SessionStatusName]}
            />
          </Show>
          <span class="pane-shortcut">{paneShortcut(kind)}</span>
        </div>
        <div class="pane-body">
          {/* One live xterm per loaded session; only the active one is shown. Keyed by session id so a
              session keeps its xterm across rail pushes — switching is pure show/hide, no reset/replay. */}
          <For each={termSessionIds()}>
            {(sid) => {
              const isActive = (): boolean => sid === activeTermSessionId();
              onCleanup(() => terminalFocus.delete(`${sid}:${pane}`));
              return (
                <div class="term-host" classList={{ hidden: !isActive() }}>
                  <TerminalView
                    slot={sid}
                    pane={pane}
                    active={isActive()}
                    onFocusReady={(focus) => terminalFocus.set(`${sid}:${pane}`, focus)}
                  />
                </div>
              );
            }}
          </For>
        </div>
      </div>
    );
  };

  const toggleBrowser = (): void => {
    setBrowserOpen((open) => !open);
  };

  // Whenever the browser is open and the active session's root listing hasn't loaded, request it; the current
  // file's ancestor folders then cascade open from there. Keyed on indexRoot() (the ACTIVE session's worktree,
  // re-pushed by the host on a switch), not the page-load primary root — so the browser follows the session.
  createEffect(() => {
    const root = indexRoot();
    if (browserOpen() && root !== null && dirListings()[root] === undefined) {
      postToHost({ type: "list-dir", path: root });
    }
  });

  // Review auto-open is decided HOST-side (it reads the session's status + change set together, so the
  // decision is race-free across a switch) and delivered as the `open` flag on `turn-changes`. The page obeys.

  onMount(() => {
    // Apply the active theme to Weavie's chrome. The controller owns the active theme + override ops and
    // also drives Monaco + xterm; this pushes the chrome's CSS vars.
    applyChromeTheme();
    mark("shell-mounted");

    // Connect to any registered remote agents so their sessions join the rail and become a New Session
    // location (best-effort; a down runner just logs and is skipped).
    connectStoredAgents();

    // The terminal panes are already in the tree and mount now — spawning claude — without waiting on
    // Monaco. The editor (a separate chunk, off the first-paint path) is brought up here; the pane shows a
    // placeholder until it resolves, with the splash held over everything until it settles.
    editor.start(editorContainer);

    const offHost = onHostMessage((message) => {
      if (editor.handleMessage(message)) {
        return;
      }
      if (message.type === "notify") {
        addToast(message.level, message.message);
      } else if (message.type === "focus-pane") {
        // The host switched the active session and asks us to land keyboard focus in a pane — Claude by
        // default, so a switch drops the user straight into the agent. The terminal xterms are persistent
        // across switches, so focusing the slot is valid even mid-respawn.
        focusPane(message.kind);
      } else if (message.type === "turn-changes") {
        // The review set (auto-keep modes). Feed the editor controller's ← / → file walk; if the host says
        // this is the moment to surface review (`open`), open the first file. No panel renders it; review is
        // the inline toolbar in the editor.
        editor.setReviewFiles(message.files);
        if (message.open) {
          editor.openFirstReviewFile();
        }
      } else if (message.type === "lsp-config") {
        // A session switch: re-point the language clients at the incoming session's LSP bridge (its own
        // worktree root), tearing the previous session's clients down.
        rebindLanguageServices(message.config);
      } else if (message.type === "dir-listing") {
        setDirListings((prev) => ({ ...prev, [message.path]: message.entries }));
      } else if (message.type === "window-state") {
        setMaximized(message.maximized);
        setWindowFocused(message.focused);
      } else if (message.type === "file-index") {
        // A switch re-pushes the index rooted at the new session's worktree. When the root changes, drop the
        // previous session's cached directory listings so the file browser re-lists the new worktree's tree
        // instead of showing the old one (listings are keyed by absolute path, so they'd otherwise linger).
        if (message.root !== indexRoot()) {
          setDirListings({});
        }
        setIndexRoot(message.root);
        setFileIndex(message.files);
      } else if (message.type === "session-delete-prompt") {
        // The host classified the worktree; open the delete dialog with the matching confirm.
        setDeleteReq({ id: message.id, label: message.label, state: message.state });
      }
      // session-status + session-list are owned by chrome/session-store (registered at module load so they
      // survive HMR); they're intentionally not handled here.
    });

    // Commands: register the web-side handlers, then install the capture-phase keybinding resolver. Ctrl+1–9
    // (focus pane by index), the omnibar focus shortcuts, the view toggles, and the inline-diff actions all
    // resolve through it; Core commands route to the host. See docs/specs/commands.md.
    // A tab command's optional `path` arg (sent by the tab context menu); absent ⇒ act on the active tab.
    const tabPath = (args: unknown): string | undefined => {
      const path = (args as { path?: unknown } | undefined)?.path;
      return typeof path === "string" ? path : undefined;
    };
    const offCommands = [
      // Returns false when there's no pane at that number, so an unbound Ctrl+digit falls through to the
      // focused xterm/Monaco.
      registerCommand(CommandIds.focusPaneByIndex, (args) => {
        const index = Number((args as { index?: unknown } | undefined)?.index);
        if (!Number.isFinite(index)) {
          return false;
        }
        const kind = paneNumbers()[index - 1];
        if (kind === undefined) {
          return false;
        }
        focusPane(kind);
        return true;
      }),
      registerCommand(CommandIds.toggleFileBrowser, () => toggleBrowser()),
      registerCommand(CommandIds.focusOmnibarFiles, () => focusOmnibar("file")),
      registerCommand(CommandIds.focusOmnibarCommands, () => focusOmnibar("command")),
      // The floating diff toolbar buttons route through these same actions, so keybindings / the palette /
      // Claude's runCommand drive the active diff identically. Each returns whether it acted, so an
      // unmatched keybinding (no active diff) falls through to the editor.
      registerCommand(CommandIds.nextChange, () => editor.inline.nextChange()),
      registerCommand(CommandIds.prevChange, () => editor.inline.prevChange()),
      registerCommand(CommandIds.acceptChange, () => editor.inline.accept()),
      registerCommand(CommandIds.rejectChange, () => editor.inline.reject()),
      registerCommand(CommandIds.undoChange, () => editor.inline.undo()),
      // Post-turn review (acceptEdits/bypass): there's no panel — these drive the inline toolbar's file axis.
      // reviewOpen jumps to the first changed file; next/prev step the review set. next/prev DECLINE (return
      // false → fall through to the editor) when no multi-file review is active, so $mod+Left/Right keep their
      // editor word-nav meaning outside a review.
      registerCommand(CommandIds.reviewOpen, () => editor.openFirstReviewFile()),
      registerCommand(CommandIds.reviewNextFile, () => editor.inline.nextFile()),
      registerCommand(CommandIds.reviewPrevFile, () => editor.inline.prevFile()),
      // Editor tabs. The targeted commands take an optional `path` (the tab context menu passes the
      // right-clicked tab; keyboard / palette omit it to act on the active tab). next/prev return whether they
      // stepped, so $mod+Tab falls through to the editor when there are <2 tabs.
      registerCommand(CommandIds.closeTab, (args) => editor.tabs.close(tabPath(args))),
      registerCommand(CommandIds.nextTab, () => editor.tabs.next()),
      registerCommand(CommandIds.prevTab, () => editor.tabs.prev()),
      registerCommand(CommandIds.closeAllTabs, () => editor.tabs.closeAll()),
      registerCommand(CommandIds.closeOtherTabs, (args) => editor.tabs.closeOthers(tabPath(args))),
      registerCommand(CommandIds.closeTabsToLeft, (args) => editor.tabs.closeToLeft(tabPath(args))),
      registerCommand(CommandIds.closeTabsToRight, (args) =>
        editor.tabs.closeToRight(tabPath(args)),
      ),
      registerCommand(CommandIds.togglePinTab, (args) => editor.tabs.togglePin(tabPath(args))),
      // New File (scratch buffer) + Save (scratch → name prompt; real file already autosaved).
      registerCommand(CommandIds.newFile, () => editor.newFile()),
      registerCommand(CommandIds.saveFile, () => editor.save()),
      // New Session… (Ctrl+Shift+N / palette / the rail's "+"): open the branch-name prompt.
      registerCommand(CommandIds.newSessionPrompt, () => setNewSessionOpen(true)),
      // Next / Previous Session (Ctrl+Shift+] / Ctrl+Shift+[): cycle the rail, wrapping around.
      registerCommand(CommandIds.nextSession, () => stepSession(1)),
      registerCommand(CommandIds.prevSession, () => stepSession(-1)),
      // Ctrl+Shift+1–9 → switch to the Nth session on the rail (the session analogue of Ctrl+1–9 for
      // panes). Returns false when there's no session at that number, so an unbound chord falls through to
      // the focused xterm/Monaco; consumes the key (returns true) when one exists, even if it's already
      // active (then it's a no-op — the host already focuses Claude on a real switch).
      registerCommand(CommandIds.selectSessionByIndex, (args) => {
        const index = Number((args as { index?: unknown } | undefined)?.index);
        if (!Number.isFinite(index)) {
          return false;
        }
        const target = sessions()[index - 1];
        if (target === undefined) {
          return false;
        }
        if (!target.active) {
          switchToSession(target);
        }
        return true;
      }),
      // Interactive delete (rail menu / palette): opens the confirm dialog after the host classifies the
      // worktree. The raw delete (weavie.session.delete) is the programmatic/MCP path.
      registerCommand(CommandIds.deleteSessionPrompt, promptDeleteSession),
    ];
    const offKeybindings = installKeybindings();
    // Double-tapping Shift mirrors $mod+P (Go to File) — a gesture the chord resolver can't express.
    const offDoubleShift = installDoubleShift(() => dispatchCommand(CommandIds.focusOmnibarFiles));

    // Track which pane holds focus (by click, Ctrl+N, or tab) for the active highlight, and publish it as a
    // `when`-context key so command guards (e.g. terminalFocused) can read it.
    const onFocusIn = (event: FocusEvent): void => {
      const slot = (event.target as HTMLElement | null)?.closest("[data-kind]");
      const kind = slot?.getAttribute("data-kind") ?? null;
      setFocusedKind(kind);
      setContext("focusedPane", kind);
      setContext("editorFocused", kind === "editor");
      setContext("terminalFocused", kind?.startsWith("terminal:") ?? false);
    };
    document.addEventListener("focusin", onFocusIn);

    onCleanup(() => {
      flushLayout.cancel();
      offKeybindings();
      offDoubleShift();
      for (const off of offCommands) {
        off();
      }
      document.removeEventListener("focusin", onFocusIn);
      offHost();
      editor.dispose();
    });
  });

  return (
    <div class="app">
      <Show when={CUSTOM_TITLEBAR}>
        <TitleBar
          maximized={maximized()}
          focused={windowFocused()}
          files={fileIndex()}
          root={indexRoot()}
          currentFile={currentFile()}
          onWindowControl={(action) => postToHost({ type: "window-control", action })}
          onMenuAction={(action, path) =>
            postToHost(
              path === undefined
                ? { type: "menu-action", action }
                : { type: "menu-action", action, path },
            )
          }
          onToggleFiles={toggleBrowser}
          onOpenFile={(path) => postToHost({ type: "reveal-file", path, line: 1 })}
          onRequestIndex={() => postToHost({ type: "request-file-index" })}
        />
      </Show>
      <Show when={CUSTOM_TITLEBAR}>
        <ResizeFrame maximized={maximized()} />
      </Show>
      <Show when={MAC_TITLEBAR}>
        <MacTitleBar
          files={fileIndex()}
          root={indexRoot()}
          currentFile={currentFile()}
          workspaceLabel={SHELL?.workspaceLabel ?? "weavie"}
          onToggleFiles={toggleBrowser}
          onOpenFile={(path) => postToHost({ type: "reveal-file", path, line: 1 })}
          onRequestIndex={() => postToHost({ type: "request-file-index" })}
        />
      </Show>
      <div class="app-body">
        <SessionRail
          sessions={sessions()}
          onSwitch={switchToSession}
          onNew={() => setNewSessionOpen(true)}
        />
        <LayoutView root={layoutRoot()} renderPane={renderPane} onResize={onLayoutResize} />
      </div>
      <Show when={newSessionOpen()}>
        <NewSessionPrompt
          onCreate={(branch, base, location) => {
            setNewSessionOpen(false);
            // Bind the page to the chosen backend first, so the worktree-creation reply (term-reset →
            // term-ready) wires the panes to it; then create the session there.
            setActiveBackendId(location);
            postToBackend(location, { type: "new-session", branch, base });
          }}
          onCheckout={(branch, location) => {
            setNewSessionOpen(false);
            // Same backend-binding order as onCreate; `existing` checks out the branch instead of creating one.
            setActiveBackendId(location);
            postToBackend(location, { type: "new-session", branch, existing: true });
          }}
          onCancel={() => setNewSessionOpen(false)}
          onAddRemote={() => {
            setNewSessionOpen(false);
            setRegisterAgentOpen(true);
          }}
        />
      </Show>
      <Show when={registerAgentOpen()}>
        <RegisterAgentModal
          onClose={() => setRegisterAgentOpen(false)}
          onAdded={() => {
            setRegisterAgentOpen(false);
            setNewSessionOpen(true);
          }}
        />
      </Show>
      <Show when={indexRoot() !== null && !HAS_TITLEBAR}>
        <button type="button" class="browser-toggle" onClick={toggleBrowser}>
          Files
        </button>
      </Show>
      <Show when={browserOpen() && indexRoot() !== null}>
        <Suspense>
          <FileBrowser
            root={indexRoot()!}
            listings={dirListings()}
            currentFile={currentFile()}
            onExpand={(path) => postToHost({ type: "list-dir", path })}
            onOpen={(path) => postToHost({ type: "reveal-file", path, line: 1 })}
            onClose={() => setBrowserOpen(false)}
          />
        </Suspense>
      </Show>
      <Toasts toasts={toasts()} onDismiss={dismissToast} />
      <Show when={confirmReq()}>
        {(req) => (
          <ConfirmDialog
            title={req().names.length > 1 ? "Discard unsaved files?" : "Discard unsaved file?"}
            body={
              req().names.length > 1
                ? `${req().names.length} unsaved scratch files will be discarded: ${req().names.join(", ")}.`
                : `"${req().names[0]}" has unsaved changes and isn't saved to a file yet. Discard it?`
            }
            confirmLabel="Discard"
            cancelLabel="Cancel"
            onConfirm={() => settleConfirm(true)}
            onCancel={() => settleConfirm(false)}
          />
        )}
      </Show>
      <Show when={deleteReq()}>
        {(req) => (
          <DeleteSessionDialog
            label={req().label}
            state={req().state}
            onConfirm={confirmDeleteSession}
            onCancel={() => setDeleteReq(null)}
          />
        )}
      </Show>
    </div>
  );
}
