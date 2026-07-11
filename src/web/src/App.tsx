import {
  createEffect,
  createMemo,
  createSignal,
  For,
  type JSX,
  lazy,
  onCleanup,
  onMount,
  Show,
  Suspense,
} from "solid-js";
import { AgentPane } from "./agent/AgentPane";
import { AgentPaneAccumulator } from "./agent/AgentPaneAccumulator";
import {
  type AgentPaneUpdate,
  activeBackendId,
  activeBackendOffline,
  activeBackendPhase,
  backendName,
  connectedBackends,
  isBrowserHostedShell,
  LOCAL_BACKEND_ID,
  onHostMessage,
  openTarget,
  postToBackend,
  postToHost,
  postToLocalHost,
  setActiveBackendId,
  type TermSession,
} from "./bridge";
import { defaultAgentProvider } from "./chrome/agent-default";
import { ContextMenu, type ContextMenuEntry, type ContextMenuState } from "./chrome/ContextMenu";
import { DeleteSessionDialog, type DeleteSessionState } from "./chrome/DeleteSessionDialog";
import { DiffAgainstPrompt } from "./chrome/DiffAgainstPrompt";
import { EditorFooter } from "./chrome/EditorFooter";
import { MacTitleBar } from "./chrome/MacTitleBar";
import { NewSessionPrompt } from "./chrome/NewSessionPrompt";
import { OpenPrPrompt } from "./chrome/OpenPrPrompt";
import { focusOmnibar } from "./chrome/omnibar-controller";
import { PaneFooter } from "./chrome/PaneFooter";
import { RegisterAgentModal } from "./chrome/RegisterAgentModal";
import { RemoteAgentsPanel } from "./chrome/RemoteAgentsPanel";
import { ResizeFrame } from "./chrome/ResizeFrame";
import { lastLocation, promoteNextSessionOn, setLastLocation } from "./chrome/rail-state";
import { agentBackendId, removeAgent } from "./chrome/remote-agents";
import { SessionRail } from "./chrome/SessionRail";
import { SourceTokenPrompt } from "./chrome/SourceTokenPrompt";
// Top-level import keeps the session store out of any hot-swapping component so the rail + active-session
// status survive HMR.
import {
  demoteSession,
  findSession,
  isPromoted,
  promoteSession,
  type RailSession,
  railSessions,
  remoteActivity,
  remoteAgentRows,
  sessions,
  sessionsReceived,
} from "./chrome/session-store";
import { suggestions } from "./chrome/suggestions-store";
import { TitleBar } from "./chrome/TitleBar";
import { UpdateOverlay } from "./chrome/UpdateOverlay";
import { UrlPrompt } from "./chrome/UrlPrompt";
import { surfacePostUpdateNotice, updateRestarting } from "./chrome/update-store";
import { writeClipboard } from "./clipboard";
import { paneFocusContext, setContext } from "./commands/context";
import { installDoubleShift } from "./commands/double-shift";
import { keyHint } from "./commands/key-hint";
import { formatKey, installKeybindings } from "./commands/keybindings";
import {
  dispatchCommand,
  getKeybindings,
  onCommandsChanged,
  registerCommand,
} from "./commands/registry";
import { CommandIds } from "./commands/types";
import { ConfirmDialog } from "./editor/ConfirmDialog";
import { EditorEmptyState } from "./editor/EditorEmptyState";
import { createEditorController } from "./editor/editor-controller";
import { basename, repoRelativePath } from "./editor/fs-path";
import MediaPane from "./editor/media/MediaPane";
import { mediaTypeOf } from "./editor/media/media-types";
import { EmbedLightbox } from "./editor/preview/EmbedLightbox";
import {
  closeEmbedZoom,
  stepEmbedZoom,
  zoomActiveEmbed,
  zoomedEmbed,
} from "./editor/preview/embed-zoom";
import { canPreview } from "./editor/preview/preview-registry";
import { SaveAsPrompt } from "./editor/SaveAsPrompt";
// Registers the set-editor-session listener at module load, before the host's one-shot restore push; the
// store otherwise lives only in the later editor chunk, so the push would arrive with no listener. Also
// keeps it alive across HMR.
import { activePath, flushEditorSession, openTabs } from "./editor/session-store";
import { activeSourceEditor } from "./editor/source/source-edit";
import {
  setSourceDoc,
  setSourceError,
  setSourceLoading,
  sourceDoc,
} from "./editor/source/source-store";
import { TabStrip } from "./editor/TabStrip";
import { isPreviewMode, toggleViewMode } from "./editor/view-mode-store";
import WebTabPane from "./editor/WebTabPane";
import { currentEditorOptions, onEditorOptionsChanged } from "./editor-options";
import type { DirListings } from "./files/FileBrowser";
import { paneOrder } from "./layout/geometry";
import { LayoutView } from "./layout/LayoutView";
import { DEFAULT_LAYOUT_ROOT, layoutDocument, sendLayout } from "./layout/store";
import type { LayoutNode } from "./layout/types";
// Session-attention intake (sounds + OS notifications): module-load side effect, like the session store.
import "./notifications/attention";
import { setNotifySink } from "./notify/notify";
import { Suggestions } from "./notify/Suggestions";
import { createToasts, Toasts } from "./notify/Toasts";
import { dismissSplash } from "./splash";
import { mark } from "./startup-timing";
import { installTerminalClipboardCommands } from "./terminal/host-clipboard";
import { TerminalView } from "./terminal/TerminalView";
import { openUrlExternal } from "./terminal/terminal-links";
import { runTestAtCursor } from "./tests/test-lens";
import { applyChromeTheme } from "./theme";

const FileBrowser = lazy(() => import("./files/FileBrowser"));
const PreviewPane = lazy(() => import("./editor/preview/PreviewPane"));
const SourceView = lazy(() => import("./editor/source/SourceView"));
const SearchPanel = lazy(() =>
  import("./chrome/SearchPanel").then((m) => ({ default: m.SearchPanel })),
);

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

const AGENT_PANE_KIND = "terminal:claude";

// Maps a terminal-backed pane kind ("terminal:claude" / "terminal:shell") to its pane id.
const paneOf = (kind: string): TermSession => (kind === AGENT_PANE_KIND ? "claude" : "shell");

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
  // Pane-switch badges show the effective focusPaneByIndex binding for their index (user-overridable in
  // keybindings.json), never a hardcoded key; empty when unbound. The version signal re-resolves them when
  // the host re-pushes the catalog (a live keybindings.json edit).
  const [keybindingsVersion, setKeybindingsVersion] = createSignal(0);
  onCleanup(onCommandsChanged(() => setKeybindingsVersion((v) => v + 1)));
  const paneShortcut = (index: number): string => {
    keybindingsVersion();
    const binding = getKeybindings().find(
      (b) =>
        b.command === CommandIds.focusPaneByIndex &&
        (b.args as { index?: number } | undefined)?.index === index,
    );
    return binding === undefined ? "" : formatKey(binding.key);
  };
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
  const [agentPaneMessages, setAgentPaneMessages] = createSignal<Record<string, AgentPaneUpdate[]>>(
    {},
  );
  const agentPaneAccumulator = new AgentPaneAccumulator((callback) =>
    requestAnimationFrame(callback),
  );
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
  const activeProviderId = createMemo<"claude" | "codex" | null>(
    () => sessions().find((s) => s.active)?.providerId ?? null,
  );
  const activeAgentSurface = createMemo<"terminal" | "structured" | "unavailable" | null>(() => {
    const session = sessions().find((s) => s.active);
    return session === undefined
      ? null
      : (session.agentSurface ?? (session.providerId === "codex" ? "structured" : "terminal"));
  });
  const activeAgentInputProtocol = createMemo(
    () => sessions().find((session) => session.active)?.agentInputProtocol ?? 1,
  );

  // Whether the "New session" prompt (branch name + base) is open; the rail's "+" opens it.
  const [newSessionOpen, setNewSessionOpen] = createSignal(false);
  const [openPrOpen, setOpenPrOpen] = createSignal(false);
  const [diffAgainstOpen, setDiffAgainstOpen] = createSignal(false);
  // The connect-a-source token dialog (host pushed prompt-source-token), or null when closed.
  const [sourceTokenPrompt, setSourceTokenPrompt] = createSignal<{
    sourceId: string;
    label: string;
  } | null>(null);
  const [registerAgentOpen, setRegisterAgentOpen] = createSignal(false);
  // The cloud panel's anchor (computed from the cloud button's rect) when open, else null.
  const [remotePanelAnchor, setRemotePanelAnchor] = createSignal<{
    left: number;
    bottom: number;
  } | null>(null);
  const [dirListings, setDirListings] = createSignal<DirListings>({});
  const [browserOpen, setBrowserOpen] = createSignal(false);
  // Whether the find-in-files (content search) panel is open; the weavie.search.findInFiles command toggles it.
  const [searchOpen, setSearchOpen] = createSignal(false);
  // Whether the "Open URL" prompt (web-tab address) is open.
  const [urlPromptOpen, setUrlPromptOpen] = createSignal(false);
  // The file currently shown in the editor, tracked so the browser can highlight + reveal it.
  const [currentFile, setCurrentFile] = createSignal<string | null>(null);
  // User-facing toasts (e.g. an autosave write that failed) — surfaced rather than silently dropped.
  const { toasts, addToast, dismissToast, dismissKeyed, isLeaving, pauseToast, resumeToast } =
    createToasts();
  // Let subsystems without an App handle (e.g. the LSP client) raise toasts for failures the user must see.
  setNotifySink(addToast);
  // Now that toasts render, surface "updated to build N" if this page load followed an update reload.
  surfacePostUpdateNotice();
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
  // A pending in-app "Save as" prompt for a scratch buffer (browser-served host, no native dialog): the
  // suggested name + the resolver the dialog settles with the chosen name (null on cancel).
  const [scratchNameReq, setScratchNameReq] = createSignal<{
    suggestedName: string;
    resolve: (name: string | null) => void;
  } | null>(null);
  const promptScratchName = (suggestedName: string): Promise<string | null> =>
    new Promise<string | null>((resolve) => setScratchNameReq({ suggestedName, resolve }));
  const settleScratchName = (name: string | null): void => {
    const req = scratchNameReq();
    if (req !== null) {
      setScratchNameReq(null);
      req.resolve(name);
    }
  };
  // The right-click menu for the editor body + terminal panes (the tab strip / rail own their own).
  const [contextMenu, setContextMenu] = createSignal<ContextMenuState | null>(null);
  // Host-pushed window chrome (maximize glyph + blur dim) and the flat workspace file index shared by the
  // omnibar's "Go to File" and the file browser. indexRoot is the ACTIVE session's worktree root — it
  // follows session switches (host re-pushes file-index on each), seeded from WORKSPACE_ROOT until the first.
  const [maximized, setMaximized] = createSignal(false);
  const [windowFocused, setWindowFocused] = createSignal(true);
  const [fileIndex, setFileIndex] = createSignal<string[]>([]);
  const [indexRoot, setIndexRoot] = createSignal<string | null>(WORKSPACE_ROOT);
  // True between a switch's index invalidation (pending file-index) and the new worktree's walked index.
  const [indexPending, setIndexPending] = createSignal(false);

  // The Monaco editor + all diff/review orchestration; App feeds it host messages and commands.
  const editor = createEditorController({
    onSaveError: (message) => addToast("error", message),
    onOpenError: (message) => addToast("warn", message),
    onCurrentFileChanged: setCurrentFile,
    confirmDiscard,
    confirm,
    promptScratchName,
  });

  // Bring the editor up once, deferred one frame past the first terminal paint so the splash-removed shell
  // reveals before the multi-MB editor chunk's eval + Monaco creation jams the main thread. Idempotent: both
  // terminal panes fire onFirstRender, plus the liveness paths below.
  let editorStarted = false;
  const startEditorOnce = (): void => {
    if (editorStarted) {
      return;
    }
    editorStarted = true;
    requestAnimationFrame(() => editor.start(editorContainer));
  };

  // Liveness: the first terminal paint is the reveal trigger, but a launch can land with NO loaded terminal to
  // paint — an all-dormant restore, or an offline remote backend — and then onFirstRender never fires. Once the
  // host has answered `ready` with its session state (sessionsReceived) and there's no active-backend terminal,
  // bring the editor up so the shell still reveals. When terminals DO exist, their paint drives it (and reveals
  // before the editor eval), so this stays out of the way — it only fires when there is nothing to jam.
  createEffect(() => {
    if (sessionsReceived() && termSessionIds().length === 0) {
      startEditorOnce();
    }
  });

  const focusPane = (kind: string): void => {
    // Mark it active first: in fullscreen this synchronously makes its slot the visible one (the others are
    // display:none), so the focus call below lands on an on-screen element rather than a hidden one.
    setActivePane(kind);
    if (kind === "editor") {
      editor.focusEditor();
      return;
    }
    if (kind === AGENT_PANE_KIND && activeAgentSurface() === "structured") {
      document.querySelector<HTMLTextAreaElement>(".agent-surface textarea")?.focus();
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

  // The active tab's path when it's a media (image/video) FILE tab and not under inline review — drives the
  // MediaPane overlay; null otherwise. The file-kind check keeps a web tab whose URL ends in .png out.
  const activeMediaPath = createMemo<string | null>(() => {
    const path = activePath();
    if (path === null || mediaTypeOf(path) === null || editor.reviewActive()) {
      return null;
    }
    const kind = openTabs().find((tab) => tab.path === path)?.kind;
    return kind === undefined || kind === "file" ? path : null;
  });

  // The active tab's URL when it's a web (iframe) tab — drives the web overlay; null otherwise.
  const activeWebUrl = createMemo<string | null>(() => {
    const path = activePath();
    if (path === null) {
      return null;
    }
    return openTabs().find((tab) => tab.path === path)?.kind === "web" ? path : null;
  });

  // The active tab's target when it's a source (Notion) tab — drives the SourceView overlay; null otherwise.
  const activeSourceTarget = createMemo<string | null>(() => {
    const path = activePath();
    if (path === null) {
      return null;
    }
    return openTabs().find((tab) => tab.path === path)?.kind === "source" ? path : null;
  });

  // Bind the page to `backendId`, then run `then` (which posts the session command). When crossing to a
  // different backend, first persist the outgoing session's unsaved edits on their own (still-active) host:
  // fs-writes route to the active backend, so flipping before the flush lands them on the wrong one — rejected
  // as out-of-worktree, the edit lost. Same-backend binds run synchronously.
  const bindBackend = (backendId: string, then: () => void): void => {
    if (backendId === activeBackendId()) {
      then();
      return;
    }
    void editor.flushDirty().finally(() => {
      setActiveBackendId(backendId);
      then();
    });
  };

  // Switch to a session by id. Flushes the outgoing session's pending editor session first so its tab set
  // isn't lost; the host processes both messages in order on the still-active session.
  const switchToSession = (session: RailSession): void => {
    flushEditorSession();
    // Crossing to another backend rebinds the page to it; its switch-session reply re-attaches terminals + editor.
    bindBackend(session.backendId, () =>
      postToBackend(session.backendId, { type: "switch-session", id: session.id }),
    );
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
    untrackedFiles: string[];
    untrackedCount: number;
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
      addToast("warn", result.error ?? "Couldn't check the session for changes.");
      return;
    }
    const info = result.data as
      | {
          state?: DeleteSessionState;
          label?: string;
          untrackedFiles?: string[];
          untrackedCount?: number;
        }
      | undefined;
    setDeleteReq({
      id,
      label: info?.label ?? id,
      state: info?.state ?? "clean",
      untrackedFiles: info?.untrackedFiles ?? [],
      untrackedCount: info?.untrackedCount ?? 0,
      backendId,
    });
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
      result.ok ? "info" : "warn",
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
          data-surface="editor"
        >
          <TabStrip
            tabs={openTabs}
            activePath={activePath}
            actions={editor.tabs}
            trailing={
              // Pane-switch badge: its own cell at the right of the tab bar (no longer floating over the tabs).
              <Show when={showPaneHints() && paneShortcut(numberOf("editor")) !== ""}>
                <span class="pane-shortcut">{paneShortcut(numberOf("editor"))}</span>
              </Show>
            }
          />
          <div class="editor-pane">
            <div
              class="editor"
              role="application"
              ref={editorContainer}
              onContextMenu={(event) => {
                // Only when a document is mounted — the empty-state pane has no selection to act on.
                if (openTabs().length === 0) {
                  return;
                }
                event.preventDefault();
                setContextMenu({
                  x: event.clientX,
                  y: event.clientY,
                  entries: [
                    { commandId: CommandIds.editorGoToDefinition },
                    { commandId: CommandIds.editorGoToReferences },
                    { commandId: CommandIds.editorRename },
                    { kind: "separator" },
                    { commandId: CommandIds.editorCut },
                    { commandId: CommandIds.editorCopy },
                    { commandId: CommandIds.editorPaste },
                    { kind: "separator" },
                    { commandId: CommandIds.focusOmnibarCommands, label: "Command Palette" },
                  ],
                });
              }}
            />
            {/* No file open: cover the blank Monaco host with an identity + keyboard-first starter actions. */}
            <Show when={openTabs().length === 0}>
              <EditorEmptyState reviewCount={editor.parkedReviewCount()} />
            </Show>
            {/* Preview mode: render the active file (Markdown) over the still-mounted Monaco host. */}
            <Show when={previewActivePath() !== null}>
              <Suspense>
                <PreviewPane content={() => editor.activeContent()} />
              </Suspense>
            </Show>
            {/* A media (image/video) file tab: render it over the still-mounted Monaco host. */}
            <Show when={activeMediaPath() !== null}>
              <MediaPane path={() => activeMediaPath() as string} />
            </Show>
            {/* A web tab: render its URL in an iframe over the still-mounted Monaco host. */}
            <Show when={activeWebUrl() !== null}>
              <WebTabPane url={() => activeWebUrl() as string} />
            </Show>
            {/* A source tab: render the fetched Notion doc as rich HTML in a shadow root over Monaco (or its
                loading spinner / fetch error while it resolves). */}
            <Show when={activeSourceTarget() !== null}>
              <Suspense>
                <SourceView
                  doc={() => sourceDoc(activeSourceTarget() as string)}
                  target={() => activeSourceTarget() as string}
                />
              </Suspense>
            </Show>
          </div>
          <EditorFooter
            onOpenRecent={(path) => editor.openFile(path, 1)}
            root={() => indexRoot() ?? ""}
          />
        </div>
      );
    }
    if (kind === AGENT_PANE_KIND && activeAgentSurface() === "structured") {
      const sid = activeTermSessionId();
      return (
        <AgentPane
          backendId={activeBackendId()}
          inputProtocol={activeAgentInputProtocol()}
          slot={sid}
          providerId={activeProviderId()}
          active={focusedKind() === AGENT_PANE_KIND}
          messages={sid === null ? [] : (agentPaneMessages()[sid] ?? [])}
          shortcut={paneShortcut(numberOf(kind))}
          onFocus={() => focusPane(kind)}
        />
      );
    }
    const pane = paneOf(kind);
    // The shell pane shows the child-set title (cwd / running command) when it has one; the agent pane stays fixed.
    const paneTitle = (): string => {
      if (kind === AGENT_PANE_KIND) {
        return "Claude Code";
      }
      const title = paneTitles()[`${activeTermSessionId()}:${pane}`];
      return title !== undefined && title.length > 0 ? title : "Terminal";
    };
    const paneSessionIds = (): string[] =>
      kind === AGENT_PANE_KIND
        ? sessions()
            .filter(
              (s) => s.loaded && s.backendId === activeBackendId() && s.providerId === "claude",
            )
            .map((s) => s.id)
        : termSessionIds();
    return (
      <div
        class="terminal-surface"
        classList={{ active: focusedKind() === kind }}
        data-kind={kind}
        data-surface="terminal"
      >
        {/* The head holds no focusable element, so a bare click would blur to <body> and strand keystrokes;
            preventDefault stops that and focusPane lands focus on this pane's xterm. The body (xterm) self-focuses. */}
        <div
          class="pane-head"
          role="toolbar"
          onMouseDown={(event) => {
            event.preventDefault();
            focusPane(kind);
          }}
        >
          <span class="pane-label">{paneTitle()}</span>
          <Show when={showPaneHints() && paneShortcut(numberOf(kind)) !== ""}>
            <span class="pane-shortcut">{paneShortcut(numberOf(kind))}</span>
          </Show>
        </div>
        <div class="pane-body">
          {/* One live xterm per loaded session, only the active shown. Keyed by session id so a session keeps
              its xterm across rail pushes — switching is pure show/hide, no reset/replay. */}
          <For each={paneSessionIds()}>
            {(sid) => {
              const isActive = (): boolean => sid === activeTermSessionId();
              onCleanup(() => terminalFocus.delete(`${sid}:${pane}`));
              return (
                <div class="term-host" classList={{ hidden: !isActive() }}>
                  <TerminalView
                    backendId={activeBackendId()}
                    slot={sid}
                    pane={pane}
                    active={isActive()}
                    onFirstRender={() => {
                      dismissSplash();
                      startEditorOnce();
                    }}
                    onFocusReady={(focus) => terminalFocus.set(`${sid}:${pane}`, focus)}
                    onTitle={(title) =>
                      setPaneTitles((prev) => ({ ...prev, [`${sid}:${pane}`]: title }))
                    }
                    onContextMenu={(event, url) => {
                      const entries: ContextMenuEntry[] = [];
                      // A URL under the pointer leads with the two ways to open it (browser is the click default).
                      if (url !== undefined) {
                        entries.push(
                          {
                            commandId: CommandIds.openUrlExternal,
                            args: { url },
                            label: "Open in Browser",
                          },
                          { commandId: CommandIds.openUrl, args: { url }, label: "Open in Weavie" },
                          { kind: "separator" },
                        );
                      }
                      entries.push({ commandId: CommandIds.terminalCopy });
                      // A served browser tab can't read the clipboard from a click (only the native paste event
                      // works there) — Ctrl+V is the paste path; the menu item only fits the WebView.
                      if (!isBrowserHostedShell()) {
                        entries.push({ commandId: CommandIds.terminalPaste });
                      }
                      entries.push(
                        { commandId: CommandIds.terminalClear },
                        { kind: "separator" },
                        { commandId: CommandIds.focusOmnibarCommands, label: "Command Palette" },
                      );
                      setContextMenu({
                        x: event.clientX,
                        y: event.clientY,
                        ...(url !== undefined ? { header: url } : {}),
                        entries,
                      });
                    }}
                  />
                </div>
              );
            }}
          </For>
        </div>
        {/* One status footer for both terminal panes, on the bottom (shell) pane; it carries the Claude
            session status too, so the Claude pane stays chrome-free below its TUI. */}
        {kind === "terminal:shell" && <PaneFooter />}
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
  const fullscreenKeyHint = (): string => keyHint(CommandIds.toggleFullscreenPane);

  // When the browser is open and the active session's root listing hasn't loaded, request it. Keyed on
  // indexRoot() (the ACTIVE session's worktree, re-pushed on a switch), so the browser follows the session.
  createEffect(() => {
    const root = indexRoot();
    if (browserOpen() && root !== null && dirListings()[root] === undefined) {
      postToHost({ type: "list-dir", path: root });
    }
  });

  onMount(() => {
    // Apply the active theme to Weavie's chrome. The controller owns the active theme + override ops and
    // also drives Monaco + xterm; this pushes the chrome's CSS vars.
    applyChromeTheme();
    mark("shell-mounted");

    // Registered remote agents are connected by remote-agents.ts when the host pushes the persisted registry on
    // `ready` (best-effort; a down runner just logs and is skipped) — no startup call needed here.

    // Occluded-launch backstop for the editor bring-up (see startEditorOnce + the liveness effect above): a
    // window hidden at launch pauses rAF, so a loaded terminal never paints its first frame and the fast path
    // never fires. Start the editor when the tab first becomes visible — no fixed timer that could fire
    // mid-reveal on a healthy launch. (A launch with zero loaded terminals is handled by the effect, which
    // isn't rAF-gated.)
    if (document.visibilityState !== "visible") {
      document.addEventListener("visibilitychange", () => startEditorOnce(), { once: true });
    }

    const offHost = onHostMessage((message) => {
      if (editor.handleMessage(message)) {
        return;
      }
      if (message.type === "notify") {
        addToast(message.level, message.message, message.key);
      } else if (message.type === "notify-clear") {
        dismissKeyed(message.key);
      } else if (message.type === "agent-pane") {
        agentPaneAccumulator.ingest(message.slot, message.message, (messages) =>
          setAgentPaneMessages((prev) => ({ ...prev, [message.slot]: messages })),
        );
      } else if (message.type === "agent-pane-reset") {
        agentPaneAccumulator.reset(message.slot, (messages) =>
          setAgentPaneMessages((prev) => ({ ...prev, [message.slot]: messages })),
        );
      } else if (message.type === "focus-pane") {
        // The host asks us to land focus in a pane (Claude by default, so a switch drops into the agent).
        // xterms persist across switches, so focusing the slot is valid even mid-respawn. Never steal from
        // an overlay input the user is typing in (the omnibar/palette, a session/PR prompt, a dialog): on a
        // slow switch this push arrives late, and yanking focus closes the palette under them mid-word. The
        // xterm helper textarea doesn't count — switching focus away FROM a terminal is the intended path.
        const active = document.activeElement;
        const typingInOverlay =
          active instanceof HTMLElement &&
          !active.classList.contains("xterm-helper-textarea") &&
          (active.tagName === "INPUT" || active.tagName === "TEXTAREA");
        if (!typingInOverlay) {
          focusPane(message.kind);
        }
      } else if (message.type === "turn-changes") {
        // The review set: feed the editor's ← / → file walk + the parked navigator, which surfaces the review
        // over the editor the moment changes land — without moving it. Stepping in is user-driven, not an
        // auto-jump. (weavie.review.open / palette still jumps on demand.) `label` names a PR/ref review ("PR
        // #12", "vs main") in the subtitle, or is empty for a plain post-turn review.
        editor.setReviewFiles(message.files, message.label);
      } else if (message.type === "lsp-config") {
        // A session switch: re-point the language clients at the incoming session's LSP bridge (its own
        // worktree root), tearing the previous session's clients down. Imported lazily — lsp-client pulls
        // Monaco, which must stay off the first-paint chunk.
        const config = message.config;
        void import("./lsp/lsp-client").then(({ rebindLanguageServices }) =>
          rebindLanguageServices(config),
        );
      } else if (message.type === "dir-listing") {
        setDirListings((prev) => ({ ...prev, [message.path]: message.entries }));
      } else if (message.type === "window-state") {
        setMaximized(message.maximized);
        setWindowFocused(message.focused);
      } else if (message.type === "file-index") {
        // A switch re-pushes the index rooted at the new worktree. On a root change, drop the cached listings
        // (keyed by absolute path, so they'd otherwise linger) and let the browser re-list the new tree. A
        // `pending` push is the walk's in-train start signal: on a root CHANGE the old session's files vanish
        // NOW (picking one would route a wrong-worktree path) and the omnibar shows loading until the walked
        // index arrives; a same-root pending (an omnibar-open refresh) keeps the still-valid current index.
        if (message.pending === true && message.root === indexRoot()) {
          return;
        }
        if (message.root !== indexRoot()) {
          setDirListings({});
        }
        setIndexRoot(message.root);
        setFileIndex(message.files);
        setIndexPending(message.pending === true);
      } else if (message.type === "prompt-source-token") {
        // The host opened the source's token page in the browser; show the dialog to paste the token.
        setSourceTokenPrompt({ sourceId: message.sourceId, label: message.label });
      } else if (message.type === "source-loading") {
        // The fetch started: open the source tab now (with a title + spinner) so the window isn't frozen while a
        // slow Notion fetch runs; source-doc / source-error fill it in.
        setSourceLoading(message.target, message.title, message.sourceId);
        editor.openSourceTab(message.target);
      } else if (message.type === "source-doc") {
        // The fetch resolved: update the entry (status → ready) and the already-open tab's SourceView renders the
        // markdown. source-loading already opened the tab, so don't re-activate here — that would yank focus back
        // if the user switched tabs during the load.
        setSourceDoc(message.target, {
          title: message.title,
          sourceId: message.sourceId,
          markdown: message.markdown,
          html: message.html,
          editedTime: message.editedTime,
          truncated: message.truncated ?? false,
          unknownBlocks: message.unknownBlocks ?? 0,
        });
      } else if (message.type === "source-error") {
        // The fetch failed: swap the open tab's spinner for the reason (no toast — the error lives in the tab).
        setSourceError(message.target, message.message);
      } else if (message.type === "source-edit-error") {
        // A block save failed: surfaced inline at the edited block (stale ⇒ the page changed, offer a re-fetch).
        // If the user left the edit behind (switched tabs) before the failure landed, toast it — a failed write
        // must reach them wherever they are, never vanish with the discarded draft.
        const shown =
          activeSourceEditor()?.showSaveError(message.target, message.message, message.stale) ??
          false;
        if (!shown) {
          addToast("error", `Notion edit failed: ${message.message}`);
        }
      } else if (message.type === "open-web") {
        // The host's resolver decided this URL isn't a source — open it as a web (iframe) tab.
        editor.openWebTab(message.url);
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
    // Copy a string derived from the target tab's path (the menu's `path` arg, else the active tab) to the
    // clipboard. Returns false (the command declines) when there's no tab to act on.
    const copyTabPath = (args: unknown, derive: (path: string) => string): boolean => {
      const path = tabPath(args) ?? activePath();
      if (path === null || path === undefined) {
        return false;
      }
      writeClipboard(derive(path));
      return true;
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
      registerCommand(CommandIds.goToSymbol, () => focusOmnibar("docSymbol")),
      registerCommand(CommandIds.goToWorkspaceSymbol, () => focusOmnibar("wsSymbol")),
      // Find in Files (Ctrl+Shift+F / palette): open the content-search panel (it focuses its input on mount).
      registerCommand(CommandIds.findInFiles, () => setSearchOpen(true)),

      // Notion block editing (source-edit.ts): the handlers return false when no source block/edit is live, so
      // the plain Enter/Escape chords fall through everywhere else.
      registerCommand(
        CommandIds.sourceEditBlock,
        () => activeSourceEditor()?.editFocusedBlock() ?? false,
      ),
      registerCommand(CommandIds.sourceCommitEdit, () => activeSourceEditor()?.commit() ?? false),
      registerCommand(CommandIds.sourceCancelEdit, () => activeSourceEditor()?.cancel() ?? false),
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
      // Comment on the current line — only a PR file under review carries a comment surface, so this DECLINES
      // (falls through) outside one.
      registerCommand(CommandIds.reviewComment, () => editor.inline.comment()),
      // Review undo/redo. The undo chords are type-split (Shift+Enter keep / Shift+Backspace revert) and decline
      // (fall through) when there's nothing of that kind to undo; redo is palette/toolbar-only.
      registerCommand(CommandIds.undoKeep, () => editor.inline.undoKeep()),
      registerCommand(CommandIds.undoRevert, () => editor.inline.undoRevert()),
      registerCommand(CommandIds.redoReview, () => editor.inline.redoReview()),
      // Post-turn review (acceptEdits/bypass): drive the inline toolbar's file axis. next/prev DECLINE (fall
      // through to the editor) when no multi-file review is active, so Ctrl+Left/Right keep Win/Linux word-nav
      // outside one.
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
      registerCommand(CommandIds.reopenClosed, () => editor.tabs.reopenClosed()),
      // Back / forward through visited editor locations (Alt+Left/Right + the back/forward mouse buttons). Each
      // returns whether it stepped, so the chord falls through to the editor when there's no history that way.
      registerCommand(CommandIds.navBack, () => editor.nav.back()),
      registerCommand(CommandIds.navForward, () => editor.nav.forward()),
      // Copy the target tab's name / repo-relative / absolute path to the clipboard (the tab menu's Copy
      // submenu; palette / Claude act on the active tab). Decline when there's no target so the chord/row
      // falls through rather than copying nothing.
      registerCommand(CommandIds.copyTabName, (args) =>
        copyTabPath(args, (path) => basename(path)),
      ),
      registerCommand(CommandIds.copyTabRelativePath, (args) =>
        copyTabPath(args, (path) => {
          const root = indexRoot();
          return root === null ? path : repoRelativePath(root, path);
        }),
      ),
      registerCommand(CommandIds.copyTabPath, (args) => copyTabPath(args, (path) => path)),
      // Editor clipboard (the right-click menu): trigger Monaco's own actions so the native chords stay Monaco's.
      registerCommand(CommandIds.editorCopy, () =>
        editor.triggerAction("editor.action.clipboardCopyAction"),
      ),
      registerCommand(CommandIds.editorCut, () =>
        editor.triggerAction("editor.action.clipboardCutAction"),
      ),
      registerCommand(CommandIds.editorPaste, () =>
        editor.triggerAction("editor.action.clipboardPasteAction"),
      ),
      // Code intelligence (right-click menu + F12 / Shift+F12 / F2): trigger Monaco's own actions, whose LSP
      // providers do the work. triggerAction returns false with no editor mounted, so the chord falls through.
      registerCommand(CommandIds.editorGoToDefinition, () =>
        editor.triggerAction("editor.action.revealDefinition"),
      ),
      registerCommand(CommandIds.editorGoToReferences, () =>
        editor.triggerAction("editor.action.goToReferences"),
      ),
      registerCommand(CommandIds.editorRename, () => editor.triggerAction("editor.action.rename")),
      // New File (scratch buffer) + Save (scratch → name prompt; real file already autosaved).
      registerCommand(CommandIds.newFile, () => editor.newFile()),
      registerCommand(CommandIds.saveFile, () => editor.save()),
      registerCommand(CommandIds.toggleEditorPreview, () => toggleActivePreview()),
      registerCommand(CommandIds.zoomEmbed, () => zoomActiveEmbed()),
      registerCommand(CommandIds.runTestAtCursor, () => {
        void runTestAtCursor();
        return true;
      }),
      // Open Folder (reuses the local host's native picker via the existing menu-action) + Open URL (opens a web tab).
      registerCommand(CommandIds.openFolder, () => {
        postToLocalHost({ type: "menu-action", action: "open-folder" });
      }),
      // Open URL: a `url` arg (the terminal's "Open in Weavie" menu / Claude) opens it in a web tab directly;
      // no arg (the palette / $mod+O) prompts. "Open in Browser" opens the same URL in the OS browser instead.
      registerCommand(CommandIds.openUrl, (args) => {
        const url = (args as { url?: unknown } | undefined)?.url;
        if (typeof url === "string" && url.length > 0) {
          openTarget(url);
        } else {
          setUrlPromptOpen(true);
        }
      }),
      registerCommand(CommandIds.openUrlExternal, (args) => {
        const url = (args as { url?: unknown } | undefined)?.url;
        if (typeof url === "string" && url.length > 0) {
          openUrlExternal(url);
        }
      }),
      // New Session… (Ctrl+Shift+N / palette / the rail's "+"): open the branch-name prompt.
      registerCommand(CommandIds.newSessionPrompt, () => setNewSessionOpen(true)),
      // Open Pull Request… (Ctrl+Shift+R / palette): pick a PR to check out as a session.
      registerCommand(CommandIds.openPr, () => setOpenPrOpen(true)),
      // Diff Against… (Ctrl+Shift+D / palette): review the working tree against a ref. A 'ref' arg (Claude /
      // a keybinding) skips the prompt; the helpers are the same flow with their ref fixed.
      registerCommand(CommandIds.diffAgainst, (args) => {
        const ref = (args as { ref?: unknown } | undefined)?.ref;
        if (typeof ref === "string" && ref.trim().length > 0) {
          postToHost({ type: "diff-against", ref: ref.trim() });
        } else {
          setDiffAgainstOpen(true);
        }
        return true;
      }),
      registerCommand(CommandIds.diffAgainstParent, () => {
        postToHost({ type: "diff-against", ref: "HEAD^" });
        return true;
      }),
      registerCommand(CommandIds.diffAgainstHead, () => {
        postToHost({ type: "diff-against", ref: "HEAD" });
        return true;
      }),
      // Next / Previous Session (Ctrl+Tab / Ctrl+Shift+Tab, gated !editorFocused so the editor's own Ctrl+Tab
      // still cycles tabs): cycle the rail, wrapping. stepSession returns false with <2 sessions so the chord
      // falls through.
      registerCommand(CommandIds.nextSession, () => stepSession(1)),
      registerCommand(CommandIds.prevSession, () => stepSession(-1)),
      // Focus Session (programmatic; the notification click-through): bring a session to the foreground by
      // 'id' (+ optional 'backendId', defaulting to the page-serving backend). Declines an unknown session.
      registerCommand(CommandIds.focusSession, (args) => {
        const a = args as { id?: unknown; backendId?: unknown } | undefined;
        if (typeof a?.id !== "string" || a.id.length === 0) {
          return false;
        }
        const backendId =
          typeof a.backendId === "string" && a.backendId.length > 0
            ? a.backendId
            : LOCAL_BACKEND_ID;
        const target = findSession(backendId, a.id);
        if (target === undefined) {
          return false;
        }
        if (!target.active) {
          switchToSession(target);
        }
        return true;
      }),
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

    // A browser tab can't read the clipboard programmatically, so terminal Paste (a clipboard read) is gated
    // off it in the command catalog — Ctrl+V there falls through to xterm's native paste instead. Session-static.
    setContext("browserShell", isBrowserHostedShell());

    // Track which pane holds focus (by click, Ctrl+N, or tab) for the active highlight, and publish it as a
    // `when`-context key so command guards (e.g. terminalFocused) can read it.
    const onFocusIn = (event: FocusEvent): void => {
      const focus = paneFocusContext(event.target as HTMLElement | null);
      const kind = typeof focus.focusedPane === "string" ? focus.focusedPane : null;
      setFocusedKind(kind);
      // Remember the last real pane (survives focus moving to the omnibar / a dialog) as the fullscreen target.
      if (kind !== null) {
        setActivePane(kind);
      }
      for (const [key, value] of Object.entries(focus)) {
        setContext(key, value);
      }
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
          filesPending={indexPending()}
          root={indexRoot()}
          currentFile={currentFile()}
          onWindowControl={(action) => postToLocalHost({ type: "window-control", action })}
          onMenuAction={(action, path) =>
            postToLocalHost(
              path === undefined
                ? { type: "menu-action", action }
                : { type: "menu-action", action, path },
            )
          }
          onToggleFiles={toggleBrowser}
          onOpenFile={(path) => postToHost({ type: "reveal-file", path, line: 1 })}
          onRequestIndex={() => postToHost({ type: "request-file-index" })}
          symbols={editor.symbols}
        />
      </Show>
      <Show when={CUSTOM_TITLEBAR}>
        <ResizeFrame maximized={maximized()} />
      </Show>
      <Show when={MAC_TITLEBAR}>
        <MacTitleBar
          files={fileIndex()}
          filesPending={indexPending()}
          root={indexRoot()}
          currentFile={currentFile()}
          workspaceLabel={SHELL?.workspaceLabel ?? "weavie"}
          onToggleFiles={toggleBrowser}
          onOpenFile={(path) => postToHost({ type: "reveal-file", path, line: 1 })}
          onRequestIndex={() => postToHost({ type: "request-file-index" })}
          symbols={editor.symbols}
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
          <Show when={fullscreen()}>
            <button
              type="button"
              class="fullscreen-exit"
              onClick={() => toggleFullscreen()}
              title={`Exit fullscreen${fullscreenKeyHint()}`}
            >
              Exit Fullscreen{fullscreenKeyHint()}
            </button>
          </Show>
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
          initialAgentProviderId={defaultAgentProvider()}
          onCreate={(branch, base, location, agentProviderId) => {
            setNewSessionOpen(false);
            setLastLocation(location);
            // A remote session lands nested under its agent; promote it onto the rail like a local one.
            promoteNextSessionOn(location);
            // Bind the page to the chosen backend first, so the worktree-creation reply (term-reset →
            // term-ready) wires the panes to it; then create the session there.
            bindBackend(location, () =>
              postToBackend(location, {
                type: "new-session",
                branch,
                base,
                agentProviderId,
              }),
            );
          }}
          onCheckout={(branch, location, agentProviderId) => {
            setNewSessionOpen(false);
            setLastLocation(location);
            promoteNextSessionOn(location);
            // Same backend-binding order as onCreate; `existing` checks out the branch instead of creating one.
            bindBackend(location, () =>
              postToBackend(location, {
                type: "new-session",
                branch,
                existing: true,
                agentProviderId,
              }),
            );
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
      <Show when={openPrOpen()}>
        <OpenPrPrompt
          backendId={defaultLocation()}
          onOpen={(target, location) => {
            setOpenPrOpen(false);
            setLastLocation(location);
            // The host's fetch→checkout→seed chain renders nothing for seconds; show a spinner toast now (keyed by
            // PR). The host clears it (notify-clear) when the diff lands, or replaces it with a keyed warn on failure.
            addToast("busy", `Opening PR #${target.number}…`, `open-pr:${target.number}`);
            // Promote + bind the backend before opening, same order as New Session, so the worktree-checkout
            // reply wires the panes to it; the host resolves the PR's branch refs by number, then checks it out.
            promoteNextSessionOn(location);
            bindBackend(location, () =>
              postToBackend(location, {
                type: "open-pr",
                number: target.number,
                owner: target.owner,
                repo: target.repo,
              }),
            );
          }}
          onCancel={() => setOpenPrOpen(false)}
        />
      </Show>
      <Show when={diffAgainstOpen()}>
        <DiffAgainstPrompt
          onPick={(ref) => {
            setDiffAgainstOpen(false);
            postToHost({ type: "diff-against", ref });
          }}
          onCancel={() => setDiffAgainstOpen(false)}
        />
      </Show>
      <Show when={sourceTokenPrompt()}>
        {(prompt) => (
          <SourceTokenPrompt
            sourceId={prompt().sourceId}
            label={prompt().label}
            onClose={() => setSourceTokenPrompt(null)}
          />
        )}
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
      <Show when={searchOpen()}>
        <Suspense>
          <SearchPanel onClose={() => setSearchOpen(false)} />
        </Suspense>
      </Show>
      <Toasts
        toasts={toasts()}
        onDismiss={dismissToast}
        isLeaving={isLeaving}
        onPause={pauseToast}
        onResume={resumeToast}
      />
      <Suggestions
        items={suggestions()}
        onDismiss={(id, forever) => postToHost({ type: "dismiss-suggestion", id, forever })}
      />
      <Show when={updateRestarting()}>
        <UpdateOverlay />
      </Show>
      <Show when={contextMenu()}>
        {(m) => <ContextMenu menu={m()} onClose={() => setContextMenu(null)} />}
      </Show>
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
      <Show when={urlPromptOpen()}>
        <UrlPrompt
          onSubmit={(url) => {
            setUrlPromptOpen(false);
            // The host resolves it: a source (Notion) renders natively; anything else comes back as a web tab.
            openTarget(url);
          }}
          onCancel={() => setUrlPromptOpen(false)}
        />
      </Show>
      <Show when={scratchNameReq()}>
        {(req) => (
          <SaveAsPrompt
            suggestedName={req().suggestedName}
            onSave={(name) => settleScratchName(name)}
            onCancel={() => settleScratchName(null)}
          />
        )}
      </Show>
      <Show when={deleteReq()}>
        {(req) => (
          <DeleteSessionDialog
            label={req().label}
            state={req().state}
            untrackedFiles={req().untrackedFiles}
            untrackedCount={req().untrackedCount}
            onConfirm={confirmDeleteSession}
            onCancel={() => setDeleteReq(null)}
          />
        )}
      </Show>
      <Show when={zoomedEmbed()}>
        {(state) => (
          <EmbedLightbox state={state()} onStep={stepEmbedZoom} onClose={closeEmbedZoom} />
        )}
      </Show>
    </div>
  );
}
