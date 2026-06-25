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
  activeBackendOffline,
  activeBackendPhase,
  backendName,
  connectedBackends,
  onHostMessage,
  postToBackend,
  postToHost,
  setActiveBackendId,
} from "./bridge";
import { DeleteSessionDialog, type DeleteSessionState } from "./chrome/DeleteSessionDialog";
import { MacTitleBar } from "./chrome/MacTitleBar";
import { NewSessionPrompt } from "./chrome/NewSessionPrompt";
import { RegisterAgentModal } from "./chrome/RegisterAgentModal";
import { RemoteAgentsPanel } from "./chrome/RemoteAgentsPanel";
import { ResizeFrame } from "./chrome/ResizeFrame";
import { SessionRail } from "./chrome/SessionRail";
import { TitleBar } from "./chrome/TitleBar";
import { focusOmnibar } from "./chrome/omnibar-controller";
import { lastLocation, promoteNextSessionOn, setLastLocation } from "./chrome/rail-state";
import { agentBackendId, removeAgent } from "./chrome/remote-agents";
// Top-level import keeps the session store out of any hot-swapping component so the rail + active-session
// status survive HMR.
import {
  type RailSession,
  claudeStatus,
  demoteSession,
  isPromoted,
  promoteSession,
  railSessions,
  remoteActivity,
  remoteAgentRows,
  sessions,
} from "./chrome/session-store";
import { setContext } from "./commands/context";
import { installDoubleShift } from "./commands/double-shift";
import { installKeybindings } from "./commands/keybindings";
import { dispatchCommand, registerCommand } from "./commands/registry";
import { CommandIds } from "./commands/types";
import { currentEditorOptions, onEditorOptionsChanged } from "./editor-options";
import { ConfirmDialog } from "./editor/ConfirmDialog";
import { EditorEmptyState } from "./editor/EditorEmptyState";
import { TabStrip } from "./editor/TabStrip";
import { createEditorController } from "./editor/editor-controller";
import { canPreview } from "./editor/preview/preview-registry";
// Registers the set-editor-session listener at module load, before the host's one-shot restore push; the
// store otherwise lives only in the later editor chunk, so the push would arrive with no listener. Also
// keeps it alive across HMR.
import { activePath, flushEditorSession, openTabs } from "./editor/session-store";
import { isPreviewMode, toggleViewMode } from "./editor/view-mode-store";
import type { DirListings } from "./files/FileBrowser";
import { LayoutView } from "./layout/LayoutView";
import { paneOrder } from "./layout/geometry";
import { DEFAULT_LAYOUT_ROOT, layoutDocument, sendLayout } from "./layout/store";
import type { LayoutNode } from "./layout/types";
import { rebindLanguageServices } from "./lsp/lsp-client";
import { Toasts, createToasts } from "./notify/Toasts";
import { setNotifySink } from "./notify/notify";
import { mark } from "./startup-timing";
import { TerminalView } from "./terminal/TerminalView";
import { installTerminalClipboardCommands } from "./terminal/host-clipboard";
import { applyChromeTheme } from "./theme";

const FileBrowser = lazy(() => import("./files/FileBrowser"));
const PreviewPane = lazy(() => import("./editor/preview/PreviewPane"));

// The PRIMARY session's workspace root (host-injected before navigation); seeds indexRoot and the "is there
// a host workspace at all" check. The live root then follows the active session. Null in plain-browser dev.
const WORKSPACE_ROOT = window.__WEAVIE_LSP__?.workspace ?? null;

// Host-injected shell config. titleBar "custom" = Windows frameless web title bar; "mac" = omnibar strip
// below the native title bar. Absent in plain-browser dev, where the floating Files button is the toggle.
const SHELL = window.__WEAVIE_SHELL__;
const CUSTOM_TITLEBAR = SHELL?.titleBar === "custom";
const MAC_TITLEBAR = SHELL?.titleBar === "mac";
// Either title-bar mode renders the omnibar + view toggles, so the floating panel buttons aren't needed.
const HAS_TITLEBAR = CUSTOM_TITLEBAR || MAC_TITLEBAR;

// Modifier label for the pane-switch shortcut badge: the ⌃ glyph on macOS, "Ctrl+" elsewhere.
const CTRL_LABEL = /Mac/i.test(navigator.userAgent) ? "⌃" : "Ctrl+";

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
  // The live pane layout tree: default-seeded, replaced by the host's persisted push, updated optimistically
  // during a splitter drag.
  const [layoutRoot, setLayoutRoot] = createSignal<LayoutNode>(DEFAULT_LAYOUT_ROOT);
  // The pane that currently has keyboard focus (tracked from focusin), for the active highlight.
  const [focusedKind, setFocusedKind] = createSignal<string | null>(null);
  // Whether the active pane is fullscreened (fills the whole pane area; the session rail stays). Pure
  // layout-view state — the saved layout is never touched, so toggling off restores it exactly.
  const [fullscreen, setFullscreen] = createSignal(false);
  // The last pane the user actually worked in (claude/shell/editor). Unlike focusedKind it survives focus
  // moving to the omnibar / a dialog, so it's the stable fullscreen target and the pane Ctrl+N switches show.
  const [activePane, setActivePane] = createSignal<string | null>(null);
  // Pane kinds in DFS order; index + 1 is the pane's Ctrl+N number. Always the REAL layout, so the numbers
  // stay stable in fullscreen.
  const paneNumbers = createMemo(() => paneOrder(layoutRoot()));
  const numberOf = (kind: string): number => paneNumbers().indexOf(kind) + 1;
  // What LayoutView renders: in fullscreen, just the active pane (filling the pane area); the others collapse
  // to display:none but stay mounted, preserving their terminal/editor state. Switching panes re-points this,
  // keeping each pane fullscreen. Off ⇒ the real layout, never mutated by fullscreen.
  const displayRoot = createMemo<LayoutNode>(() => {
    const kind = activePane();
    return fullscreen() && kind !== null ? { type: "pane", id: "fullscreen", kind } : layoutRoot();
  });
  // Each loaded session's terminal panes register their focus fn here on mount, keyed `${slot}:${pane}`;
  // focusPane resolves the active session's entry. (The editor focuses via the controller directly.)
  const terminalFocus = new Map<string, () => void>();
  // The child-set terminal title (OSC 0/2) per `${slot}:${pane}`, shown in the shell pane header (the claude
  // pane keeps its fixed "Claude Code" label).
  const [paneTitles, setPaneTitles] = createSignal<Record<string, string>>({});
  // Whether the Ctrl+N pane-switch hint badges are shown (the editor.paneShortcutHints setting; live-updated).
  const [showPaneHints, setShowPaneHints] = createSignal(currentEditorOptions().paneShortcutHints);

  // A stable string[] of the active backend's loaded session ids, so <For> never remounts a session's
  // terminals across rail pushes — keeping them alive makes a switch pure show/hide. Excludes dormant and
  // other-backend sessions.
  const termSessionIds = createMemo(() =>
    sessions()
      .filter((s) => s.loaded && s.backendId === activeBackendId())
      .map((s) => s.id),
  );
  // The session whose panes are shown (null before the first rail push); flipping it switches which
  // session's terminals are visible.
  const activeTermSessionId = createMemo(() => sessions().find((s) => s.active)?.id ?? null);

  // Whether the "New session" prompt (branch name + base) is open; the rail's "+" opens it.
  const [newSessionOpen, setNewSessionOpen] = createSignal(false);
  const [registerAgentOpen, setRegisterAgentOpen] = createSignal(false);
  // The cloud panel's anchor (computed from the cloud button's rect) when open, else null.
  const [remotePanelAnchor, setRemotePanelAnchor] = createSignal<{
    left: number;
    bottom: number;
  } | null>(null);
  const [dirListings, setDirListings] = createSignal<DirListings>({});
  const [browserOpen, setBrowserOpen] = createSignal(false);
  // The file currently shown in the editor, tracked so the browser can highlight + reveal it.
  const [currentFile, setCurrentFile] = createSignal<string | null>(null);
  // User-facing toasts (e.g. an autosave write that failed) — surfaced rather than silently dropped.
  const { toasts, addToast, dismissToast, isLeaving } = createToasts();
  // Let subsystems without an App handle (e.g. the LSP client) raise toasts for failures the user must see.
  setNotifySink(addToast);
  // A pending "discard unsaved scratch?" confirm: the names + the resolver the dialog settles. Every tab
  // close routes through this guard (confirmDiscard below).
  const [confirmReq, setConfirmReq] = createSignal<{
    title: string;
    body: string;
    confirmLabel: string;
    resolve: (ok: boolean) => void;
  } | null>(null);
  const confirm = (options: {
    title: string;
    body: string;
    confirmLabel: string;
  }): Promise<boolean> => new Promise<boolean>((resolve) => setConfirmReq({ ...options, resolve }));
  const confirmDiscard = (names: string[]): Promise<boolean> =>
    confirm({
      title: names.length > 1 ? "Discard unsaved files?" : "Discard unsaved file?",
      body:
        names.length > 1
          ? `${names.length} unsaved scratch files will be discarded: ${names.join(", ")}.`
          : `"${names[0]}" has unsaved changes and isn't saved to a file yet. Discard it?`,
      confirmLabel: "Discard",
    });
  const settleConfirm = (ok: boolean): void => {
    const req = confirmReq();
    if (req !== null) {
      setConfirmReq(null);
      req.resolve(ok);
    }
  };
  // Host-pushed window chrome (maximize glyph + blur dim) and the flat workspace file index shared by the
  // omnibar's "Go to File" and the file browser. indexRoot is the ACTIVE session's worktree root — it
  // follows session switches (host re-pushes file-index on each), seeded from WORKSPACE_ROOT until the first.
  const [maximized, setMaximized] = createSignal(false);
  const [windowFocused, setWindowFocused] = createSignal(true);
  const [fileIndex, setFileIndex] = createSignal<string[]>([]);
  const [indexRoot, setIndexRoot] = createSignal<string | null>(WORKSPACE_ROOT);

  // The Monaco editor + all diff/review orchestration; App feeds it host messages and commands.
  const editor = createEditorController({
    onSaveError: (message) => addToast("error", message),
    onOpenError: (message) => addToast("error", message),
    onCurrentFileChanged: setCurrentFile,
    confirmDiscard,
    confirm,
  });

  const focusPane = (kind: string): void => {
    // Mark it active first: in fullscreen this synchronously makes its slot the visible one (the others are
    // display:none), so the focus call below lands on an on-screen element rather than a hidden one.
    setActivePane(kind);
    if (kind === "editor") {
      editor.focusEditor();
      return;
    }
    // Resolve the focusable xterm by the active session id, so focus lands correctly regardless of
    // effect-flush timing on a switch.
    const pane = paneOf(kind);
    const sid = activeTermSessionId();
    if (sid !== null) {
      terminalFocus.get(`${sid}:${pane}`)?.();
    }
  };

  // Flip the active file between Source and Preview, only when its type can preview. Returns whether it acted,
  // so the command DECLINES (key falls through to the editor) on a non-previewable file.
  const toggleActivePreview = (): boolean => {
    const path = activePath();
    if (path === null || !canPreview(path)) {
      return false;
    }
    // Returning to Source hands focus back to Monaco; the Preview overlay focuses itself on mount.
    if (toggleViewMode(path) === "source") {
      editor.focusEditor();
    }
    return true;
  };

  // The active file's path when it's previewable, in Preview mode, and not under inline review (which owns the
  // editor) — drives the Preview overlay; null otherwise.
  const previewActivePath = createMemo<string | null>(() => {
    const path = activePath();
    return path !== null && canPreview(path) && isPreviewMode(path) && !editor.reviewActive()
      ? path
      : null;
  });

  // Switch to a session by id. Flushes the outgoing session's pending editor session first so its tab set
  // isn't lost; the host processes both messages in order on the still-active session.
  const switchToSession = (session: RailSession): void => {
    flushEditorSession();
    // Crossing to another backend rebinds the page to it; its switch-session reply re-attaches terminals + editor.
    if (session.backendId !== activeBackendId()) {
      setActiveBackendId(session.backendId);
    }
    postToBackend(session.backendId, { type: "switch-session", id: session.id });
  };

  // The active backend's human name for the reconnecting banner ("the host" for the local headless link).
  const connectionLabel = (): string =>
    activeBackendId() === "local" ? "the host" : backendName(activeBackendId());

  // The location to preselect in the New Session prompt: the last-used backend if still connected, else
  // local (a remembered agent that failed to reconnect falls back rather than picking a dead id).
  const defaultLocation = (): string => {
    const last = lastLocation();
    return connectedBackends().some((b) => b.id === last) ? last : "local";
  };

  // Step the active session to the next/prev LOADED rail chip (delta ±1, wraps); dormant chips are skipped.
  // Returns whether it stepped, so with <2 loaded sessions the keystroke falls through (matching tab next/prev).
  const stepSession = (delta: number): boolean => {
    const list = railSessions().filter((s) => s.loaded);
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

  // A pending session delete, opened once weavie.session.delete (classify mode) returns the worktree state and
  // DeleteSessionDialog raises the matching confirm (clean / untracked / modified). `backendId` is the owning
  // host, so a remote session deleted from the cloud panel routes its classify + delete back to it.
  const [deleteReq, setDeleteReq] = createSignal<{
    id: string;
    label: string;
    state: DeleteSessionState;
    backendId: string;
  } | null>(null);
  // Interactive delete (rail menu / cloud panel / palette): no args targets the active session. Classify the
  // OWNING backend's worktree (weavie.session.delete with classify) to open the dialog at the right escalation.
  const promptDeleteSession = async (args: unknown): Promise<void> => {
    const a = args as { id?: string; backendId?: string } | undefined;
    const active = sessions().find((s) => s.active);
    const id = a?.id ?? active?.id;
    const backendId = a?.backendId ?? active?.backendId ?? "local";
    if (id === undefined) {
      return;
    }
    const result = await dispatchCommand(CommandIds.deleteSession, {
      id,
      backendId,
      classify: true,
    });
    if (!result.ok) {
      addToast("error", result.error ?? "Couldn't check the session for changes.");
      return;
    }
    const info = result.data as { state?: DeleteSessionState; label?: string } | undefined;
    setDeleteReq({ id, label: info?.label ?? id, state: info?.state ?? "clean", backendId });
  };
  const confirmDeleteSession = async (): Promise<void> => {
    const req = deleteReq();
    if (req === null) {
      return;
    }
    setDeleteReq(null);
    // A dirty worktree (untracked or modified) needs force, or git refuses the removal.
    const result = await dispatchCommand(CommandIds.deleteSession, {
      id: req.id,
      backendId: req.backendId,
      force: req.state !== "clean",
    });
    addToast(
      result.ok ? "info" : "error",
      result.ok
        ? (result.message ?? "Session deleted.")
        : (result.error ?? "Couldn't delete the session."),
    );
  };

  // Persist the layout after a user gesture (debounced). Skipped until the host's initial layout push, so we
  // never overwrite the saved state with the default before it loads.
  let persistTimer = 0;
  const persistRoot = (root: LayoutNode): void => {
    const base = layoutDocument();
    if (base === null) {
      return;
    }
    window.clearTimeout(persistTimer);
    persistTimer = window.setTimeout(() => {
      sendLayout({ ...base, root });
    }, 400);
  };

  // A splitter drag: show the new sizes immediately, persist on a debounce.
  const onLayoutResize = (root: LayoutNode): void => {
    setLayoutRoot(root);
    persistRoot(root);
  };

  // Apply the host-pushed layout (startup restore + any later host/MCP change). The resize handler is
  // gesture-driven, so a pushed layout never echoes back into a save.
  createEffect(() => {
    const doc = layoutDocument();
    if (doc !== null) {
      setLayoutRoot(doc.root);
    }
  });

  // Renders the surface for a pane kind. Called once per kind by LayoutView (the slot list is stable), so
  // each surface is created once and only repositioned. Within a terminal kind, one xterm per loaded session
  // is mounted (only the active shown) — see the For below.
  const renderPane = (kind: string): JSX.Element => {
    if (kind === "editor") {
      return (
        <div
          class="editor-surface"
          classList={{ active: focusedKind() === "editor" }}
          data-kind="editor"
        >
          <TabStrip
            tabs={openTabs}
            activePath={activePath}
            actions={editor.tabs}
            trailing={
              // Pane-switch badge: its own cell at the right of the tab bar (no longer floating over the tabs).
              <Show when={showPaneHints()}>
                <span class="pane-shortcut">
                  {CTRL_LABEL}
                  {numberOf("editor")}
                </span>
              </Show>
            }
          />
          <div class="editor-pane">
            <div class="editor" ref={editorContainer} />
            {/* No file open: cover the blank Monaco host with an identity + keyboard-first starter actions. */}
            <Show when={openTabs().length === 0}>
              <EditorEmptyState />
            </Show>
            {/* Preview mode: render the active file (Markdown) over the still-mounted Monaco host. */}
            <Show when={previewActivePath() !== null}>
              <Suspense>
                <PreviewPane content={() => editor.activeContent()} />
              </Suspense>
            </Show>
          </div>
        </div>
      );
    }
    const pane = paneOf(kind);
    // The shell pane shows the child-set title (cwd / running command) when it has one; claude stays fixed.
    const paneTitle = (): string => {
      if (kind === "terminal:claude") {
        return "Claude Code";
      }
      const title = paneTitles()[`${activeTermSessionId()}:${pane}`];
      return title !== undefined && title.length > 0 ? title : "Terminal";
    };
    return (
      <div class="terminal-surface" classList={{ active: focusedKind() === kind }} data-kind={kind}>
        {/* The head holds no focusable element, so a bare click would blur to <body> and strand keystrokes;
            preventDefault stops that and focusPane lands focus on this pane's xterm. The body (xterm) self-focuses. */}
        <div
          class="pane-head"
          onMouseDown={(event) => {
            event.preventDefault();
            focusPane(kind);
          }}
        >
          <span class="pane-label">{paneTitle()}</span>
          <Show when={kind === "terminal:claude" && claudeStatus() !== undefined}>
            <span
              class={`session-status status-${claudeStatus()}`}
              title={STATUS_LABEL[claudeStatus() as SessionStatusName]}
            />
          </Show>
          <Show when={showPaneHints()}>
            <span class="pane-shortcut">
              {CTRL_LABEL}
              {numberOf(kind)}
            </span>
          </Show>
        </div>
        <div class="pane-body">
          {/* One live xterm per loaded session, only the active shown. Keyed by session id so a session keeps
              its xterm across rail pushes — switching is pure show/hide, no reset/replay. */}
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
                    onTitle={(title) =>
                      setPaneTitles((prev) => ({ ...prev, [`${sid}:${pane}`]: title }))
                    }
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

  // Fullscreen the active pane (Toggle Fullscreen Pane command). Entering with nothing focused yet lands on
  // the first pane so there's always something to fill the view; the session rail stays (it's outside LayoutView).
  const toggleFullscreen = (): void => {
    if (!fullscreen() && activePane() === null) {
      const first = paneNumbers()[0];
      if (first !== undefined) {
        focusPane(first);
      }
    }
    setFullscreen((on) => !on);
  };

  // When the browser is open and the active session's root listing hasn't loaded, request it. Keyed on
  // indexRoot() (the ACTIVE session's worktree, re-pushed on a switch), so the browser follows the session.
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

    // Registered remote agents are connected by remote-agents.ts when the host pushes the persisted registry on
    // `ready` (best-effort; a down runner just logs and is skipped) — no startup call needed here.

    // Terminal panes mount independently (spawning claude). The editor is a separate off-first-paint chunk
    // brought up here; its pane shows a placeholder until it resolves, splash held until it settles.
    editor.start(editorContainer);

    const offHost = onHostMessage((message) => {
      if (editor.handleMessage(message)) {
        return;
      }
      if (message.type === "notify") {
        addToast(message.level, message.message);
      } else if (message.type === "focus-pane") {
        // The host asks us to land focus in a pane (Claude by default, so a switch drops into the agent).
        // xterms persist across switches, so focusing the slot is valid even mid-respawn.
        focusPane(message.kind);
      } else if (message.type === "turn-changes") {
        // The review set (auto-keep modes): feed the editor's ← / → file walk; on `open`, surface review by
        // opening the first file. Review is the inline editor toolbar, not a panel.
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
        // A switch re-pushes the index rooted at the new worktree. On a root change, drop the cached listings
        // (keyed by absolute path, so they'd otherwise linger) and let the browser re-list the new tree.
        if (message.root !== indexRoot()) {
          setDirListings({});
        }
        setIndexRoot(message.root);
        setFileIndex(message.files);
      }
      // session-status + session-list are owned by chrome/session-store (registered at module load so they
      // survive HMR); they're intentionally not handled here.
    });

    // Commands: register the web-side handlers, then install the capture-phase keybinding resolver. Core
    // commands route to the host. See docs/specs/commands.md.
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
        // Re-pressing the editor's focus number while it's already focused toggles Source/Preview (on a
        // non-previewable file toggleActivePreview declines, so this just re-focuses the editor).
        if (kind === "editor" && focusedKind() === "editor" && toggleActivePreview()) {
          return true;
        }
        focusPane(kind);
        return true;
      }),
      registerCommand(CommandIds.toggleFullscreenPane, () => toggleFullscreen()),
      registerCommand(CommandIds.toggleFileBrowser, () => toggleBrowser()),
      // Terminal copy/paste (act on the focused xterm, clipboard via the host); gated terminalFocused.
      installTerminalClipboardCommands(),
      registerCommand(CommandIds.focusOmnibarFiles, () => focusOmnibar("file")),
      registerCommand(CommandIds.focusOmnibarCommands, () => focusOmnibar("command")),
      // The floating diff toolbar buttons route through these same actions. Each returns whether it acted, so
      // an unmatched keybinding (no active diff) falls through to the editor.
      registerCommand(CommandIds.nextChange, () => editor.inline.nextChange()),
      registerCommand(CommandIds.prevChange, () => editor.inline.prevChange()),
      registerCommand(CommandIds.acceptChange, () => editor.inline.accept()),
      registerCommand(CommandIds.rejectChange, () => editor.inline.reject()),
      registerCommand(CommandIds.undoChange, () => editor.inline.undo()),
      registerCommand(CommandIds.keepFile, () => editor.inline.keepFile()),
      registerCommand(CommandIds.revertFile, () => editor.inline.revertFile()),
      registerCommand(CommandIds.keepAll, () => editor.inline.keepAll()),
      // Post-turn review (acceptEdits/bypass): drive the inline toolbar's file axis. next/prev DECLINE (fall
      // through to the editor) when no multi-file review is active, so $mod+Left/Right keep word-nav outside one.
      registerCommand(CommandIds.reviewOpen, () => editor.openFirstReviewFile()),
      registerCommand(CommandIds.reviewNextFile, () => editor.inline.nextFile()),
      registerCommand(CommandIds.reviewPrevFile, () => editor.inline.prevFile()),
      // Editor tabs. Targeted commands take an optional `path` (the context menu's right-clicked tab; keyboard
      // / palette omit it for the active tab). next/prev return whether they stepped, so Ctrl+Tab falls
      // through to the editor with <2 tabs.
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
      registerCommand(CommandIds.toggleEditorPreview, () => toggleActivePreview()),
      // New Session… (Ctrl+Shift+N / palette / the rail's "+"): open the branch-name prompt.
      registerCommand(CommandIds.newSessionPrompt, () => setNewSessionOpen(true)),
      // Next / Previous Session (Ctrl+Tab / Ctrl+Shift+Tab, gated !editorFocused so the editor's own Ctrl+Tab
      // still cycles tabs): cycle the rail, wrapping. stepSession returns false with <2 sessions so the chord
      // falls through.
      registerCommand(CommandIds.nextSession, () => stepSession(1)),
      registerCommand(CommandIds.prevSession, () => stepSession(-1)),
      // Ctrl+Shift+1–9 → switch to the Nth rail session. Returns false when there's none at that number (the
      // chord falls through); consumes the key when one exists, even if already active (then a no-op).
      registerCommand(CommandIds.selectSessionByIndex, (args) => {
        const index = Number((args as { index?: unknown } | undefined)?.index);
        if (!Number.isFinite(index)) {
          return false;
        }
        const target = railSessions()[index - 1];
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
      // Disconnect a remote agent (rail right-click): close its bridge + forget it (the registry is
      // client-side). Declines a missing/blank name.
      registerCommand(CommandIds.disconnectRemoteAgent, (args) => {
        const name = (args as { agent?: unknown } | undefined)?.agent;
        if (typeof name !== "string" || name.length === 0) {
          return false;
        }
        removeAgent(name);
        return true;
      }),
      // Remove a promoted remote session from the rail's working set (rail right-click on a remote chip).
      registerCommand(CommandIds.removeFromRail, (args) => {
        const a = args as { backendId?: unknown; id?: unknown } | undefined;
        if (typeof a?.backendId !== "string" || typeof a?.id !== "string") {
          return false;
        }
        demoteSession(a.backendId, a.id);
        return true;
      }),
    ];
    // Live-track the pane-hint setting so toggling editor.paneShortcutHints shows/hides the badges at once.
    const offEditorOptions = onEditorOptionsChanged((options) =>
      setShowPaneHints(options.paneShortcutHints),
    );
    const offKeybindings = installKeybindings();
    // Double-tapping Shift mirrors $mod+P (Go to File) — a gesture the chord resolver can't express.
    const offDoubleShift = installDoubleShift(() => dispatchCommand(CommandIds.focusOmnibarFiles));

    // Track which pane holds focus (by click, Ctrl+N, or tab) for the active highlight, and publish it as a
    // `when`-context key so command guards (e.g. terminalFocused) can read it.
    const onFocusIn = (event: FocusEvent): void => {
      const slot = (event.target as HTMLElement | null)?.closest("[data-kind]");
      const kind = slot?.getAttribute("data-kind") ?? null;
      setFocusedKind(kind);
      // Remember the last real pane (survives focus moving to the omnibar / a dialog) as the fullscreen target.
      if (kind !== null) {
        setActivePane(kind);
      }
      setContext("focusedPane", kind);
      setContext("editorFocused", kind === "editor");
      setContext("terminalFocused", kind?.startsWith("terminal:") ?? false);
    };
    document.addEventListener("focusin", onFocusIn);

    onCleanup(() => {
      window.clearTimeout(persistTimer);
      offEditorOptions();
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
          sessions={railSessions()}
          hasRemotes={remoteAgentRows().length > 0}
          remoteActive={remoteActivity()}
          onSwitch={switchToSession}
          onNew={() => setNewSessionOpen(true)}
          onToggleRemotes={(rect) =>
            setRemotePanelAnchor((open) =>
              open !== null
                ? null
                : { left: rect.right + 6, bottom: window.innerHeight - rect.bottom },
            )
          }
        />
        <div class="pane-area" classList={{ offline: activeBackendOffline() }}>
          <LayoutView root={displayRoot()} renderPane={renderPane} onResize={onLayoutResize} />
          <Show when={activeBackendOffline()}>
            <output class="connection-banner">
              <span class="connection-spinner" aria-hidden="true" />
              <span>
                {activeBackendPhase() === "connecting" ? "Connecting to " : "Reconnecting to "}
                {connectionLabel()}…
              </span>
            </output>
          </Show>
        </div>
      </div>
      <Show when={newSessionOpen()}>
        <NewSessionPrompt
          initialBackendId={defaultLocation()}
          onCreate={(branch, base, location) => {
            setNewSessionOpen(false);
            setLastLocation(location);
            // A remote session lands nested under its agent; promote it onto the rail like a local one.
            promoteNextSessionOn(location);
            // Bind the page to the chosen backend first, so the worktree-creation reply (term-reset →
            // term-ready) wires the panes to it; then create the session there.
            setActiveBackendId(location);
            postToBackend(location, { type: "new-session", branch, base });
          }}
          onCheckout={(branch, location) => {
            setNewSessionOpen(false);
            setLastLocation(location);
            promoteNextSessionOn(location);
            // Same backend-binding order as onCreate; `existing` checks out the branch instead of creating one.
            setActiveBackendId(location);
            postToBackend(location, { type: "new-session", branch, existing: true });
          }}
          onCancel={() => setNewSessionOpen(false)}
          onAddRemote={() => {
            setNewSessionOpen(false);
            setRegisterAgentOpen(true);
          }}
          onDisconnect={(backendId) => {
            const name = connectedBackends().find((b) => b.id === backendId)?.name;
            if (name !== undefined) {
              removeAgent(name);
            }
          }}
        />
      </Show>
      <Show when={registerAgentOpen()}>
        <RegisterAgentModal
          onClose={() => setRegisterAgentOpen(false)}
          onAdded={(name) => {
            setRegisterAgentOpen(false);
            // Preselect the just-added agent as the next prompt's location (it connected before onAdded fired).
            setLastLocation(agentBackendId(name));
            setNewSessionOpen(true);
          }}
        />
      </Show>
      <Show when={remotePanelAnchor()}>
        {(anchor) => (
          <RemoteAgentsPanel
            agents={remoteAgentRows()}
            anchor={anchor()}
            isPromoted={isPromoted}
            onPick={(session) => {
              // Pull the picked remote session into the rail and switch to it.
              promoteSession(session.backendId, session.id);
              switchToSession(session);
              setRemotePanelAnchor(null);
            }}
            onDisconnect={(name) => removeAgent(name)}
            onAddRemote={() => {
              setRemotePanelAnchor(null);
              setRegisterAgentOpen(true);
            }}
            onClose={() => setRemotePanelAnchor(null)}
          />
        )}
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
      <Toasts toasts={toasts()} onDismiss={dismissToast} isLeaving={isLeaving} />
      <Show when={confirmReq()}>
        {(req) => (
          <ConfirmDialog
            title={req().title}
            body={req().body}
            confirmLabel={req().confirmLabel}
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
