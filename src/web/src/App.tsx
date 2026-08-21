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
import { toggleActiveAgentMermaid } from "./agent/agent-mermaid";
import {
  type AgentPaneModel,
  agentAuthenticationTerminalActive,
  agentPaneModel,
} from "./agent/pane-store";
import {
  activeBackendOffline,
  activeBackendPhase,
  backendName,
  backendPhase,
  beginClientSelection,
  type ClientSession,
  clientSession,
  connectedBackends,
  hostConnection,
  invokeSessionCommandOnBackend,
  isBrowserHostedShell,
  LOCAL_BACKEND_ID,
  registerViewFeature,
  selectedSession,
  type TermSession,
  waitForClientSession,
} from "./bridge";
import { AcpRegistryModal } from "./chrome/AcpRegistryModal";
import { defaultAgentProvider, setDefaultAgentProvider } from "./chrome/agent-default";
import { ContextMenu, type ContextMenuEntry, type ContextMenuState } from "./chrome/ContextMenu";
import { DeleteSessionDialog, type DeleteSessionState } from "./chrome/DeleteSessionDialog";
import { DiffAgainstPrompt } from "./chrome/DiffAgainstPrompt";
import { EditorFooter } from "./chrome/EditorFooter";
import { gitStatus } from "./chrome/git-status-store";
import { installMiddleClickAutoscroll } from "./chrome/middle-click-autoscroll";
import { NativeTitleBar } from "./chrome/NativeTitleBar";
import { OpenPrPrompt } from "./chrome/OpenPrPrompt";
import { focusOmnibar, focusOmnibarFileSearch } from "./chrome/omnibar-controller";
import { PaneFooter } from "./chrome/PaneFooter";
import type { PopoverAnchor } from "./chrome/popover-position";
import { pullRequestStatus } from "./chrome/pull-request-store";
import { RegisterAgentModal } from "./chrome/RegisterAgentModal";
import { RemoteAgentsPanel } from "./chrome/RemoteAgentsPanel";
import { ResizeFrame } from "./chrome/ResizeFrame";
import { lastLocation, promoteNextSessionOn, setLastLocation } from "./chrome/rail-state";
import { agentBackendId, removeAgent } from "./chrome/remote-agents";
import { SessionRail } from "./chrome/SessionRail";
import { SourceTokenPrompt } from "./chrome/SourceTokenPrompt";
import {
  seedSearch,
  setSearchOpener,
  stepSearchResult,
  toggleSearchOption,
} from "./chrome/search-store";
// Top-level import keeps the session store out of any hot-swapping component so the rail + selected-session
// status survive HMR.
import {
  beginSessionSelection,
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
  stepRailTarget,
} from "./chrome/session-store";
import { suggestions } from "./chrome/suggestions-store";
import { TitleBar } from "./chrome/TitleBar";
import { UpdateOverlay } from "./chrome/UpdateOverlay";
import { UrlPrompt } from "./chrome/UrlPrompt";
import {
  activeBackendBuildMismatch,
  surfacePostUpdateNotice,
  updateRestarting,
} from "./chrome/update-store";
import { hostWindowFocused, windowMaximized } from "./chrome/window-state";
import { writeClipboard } from "./clipboard";
import { paneFocusContext, setContext } from "./commands/context";
import { installDoubleShift } from "./commands/double-shift";
import { keyHint } from "./commands/key-hint";
import { formatKey, installKeybindings } from "./commands/keybindings";
import {
  applySessionActivation,
  dispatchCommand,
  dispatchCommandFromCatalog,
  getKeybindings,
  onCommandsChanged,
  onSessionActivated,
  registerCommand,
} from "./commands/registry";
import { CommandIds } from "./commands/types";
import { BlamePopover } from "./editor/BlamePopover";
import { blameTarget } from "./editor/blame-store";
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
// Registers the per-session editor restore listener before the host's sync response; the
// store otherwise lives only in the later editor chunk, so the push would arrive with no listener. Also
// keeps it alive across HMR.
import {
  activePath,
  activePathFor,
  flushEditorSession,
  openTabs,
  openTabsFor,
} from "./editor/session-store";
import { activeSourceEditor } from "./editor/source/source-edit";
import {
  dismissSourceTokenPrompt,
  onSourceEditError,
  openSelectedSourceTarget,
  selectedSourceTokenPrompt,
  sourceDoc,
} from "./editor/source/source-store";
import { TabStrip } from "./editor/TabStrip";
import { isPreviewMode, toggleViewMode } from "./editor/view-mode-store";
import WebTabPane from "./editor/WebTabPane";
import { currentEditorOptions, onEditorOptionsChanged } from "./editor-options";
import {
  listSelectedDirectory,
  refreshSelectedFileIndex,
  revealSelectedFile,
  selectedDirectoryListings,
  selectedFileIndex,
} from "./files/session-files";
import { paneOrder } from "./layout/geometry";
import { LayoutView } from "./layout/LayoutView";
import { DEFAULT_LAYOUT_ROOT, layoutDocument, sendLayout } from "./layout/store";
import type { LayoutNode } from "./layout/types";
import type { MobileSurface, MobileSwipeDirection } from "./mobile/MobileSurfaceBar";
import { MobileWorkspace } from "./mobile/MobileWorkspace";
import { createMobileBackSwipe } from "./mobile/mobile-back-swipe";
import { createMobileHistory } from "./mobile/mobile-history";
import { createMobileVisualViewportStyle } from "./mobile/mobile-visual-viewport";
import { useCompactMode } from "./mobile/useCompactMode";
// Session-attention intake (sounds + OS notifications): module-load side effect, like the session store.
import "./notifications/attention";
import "./notifications/intake";
import { setNotifySink } from "./notify/notify";
import { Suggestions } from "./notify/Suggestions";
import { createToasts, Toasts } from "./notify/Toasts";
import { dismissSplash } from "./splash";
import { mark } from "./startup-timing";
import { installTerminalClipboardCommands } from "./terminal/host-clipboard";
import { TerminalView } from "./terminal/TerminalView";
import { openUrlExternal } from "./terminal/terminal-links";
import { applyChromeTheme } from "./theme";

const FileBrowser = lazy(() => import("./files/FileBrowser"));
const PlanView = lazy(() => import("./editor/plan/PlanView"));
const PreviewPane = lazy(() => import("./editor/preview/PreviewPane"));
const SourceView = lazy(() => import("./editor/source/SourceView"));
const SearchPanel = lazy(() =>
  import("./chrome/SearchPanel").then((m) => ({ default: m.SearchPanel })),
);

// Host-injected shell config. "custom" is the Windows frameless title bar; macOS/Linux render an app bar
// below their native frame. Absent in plain-browser dev, where the floating Files button is the toggle.
const SHELL = window.__WEAVIE_SHELL__;
const CUSTOM_TITLEBAR = SHELL?.titleBar === "custom";
const MAC_TITLEBAR = SHELL?.titleBar === "mac";
const LINUX_TITLEBAR = SHELL?.titleBar === "linux";
const NATIVE_SHELL = ["win", "mac", "linux"].includes(SHELL?.platform ?? "");
// Every app-bar mode renders the omnibar + view toggles, so the floating panel buttons aren't needed.
const HAS_TITLEBAR = CUSTOM_TITLEBAR || MAC_TITLEBAR || LINUX_TITLEBAR;
setContext("nativeShell", NATIVE_SHELL);

const AGENT_PANE_KIND = "terminal:claude";
const REDUCED_MOTION = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
// Maps a terminal-backed pane kind ("terminal:claude" / "terminal:shell") to its pane id.
const paneOf = (kind: string): TermSession => (kind === AGENT_PANE_KIND ? "claude" : "shell");

interface MobileTransition {
  direction: MobileSwipeDirection;
  navigation: "back" | "select";
  phase: "tracking" | "canceling" | "committing";
  progress: number;
  source: MobileSurface;
  target: MobileSurface;
}

function mobileTransitionStyle(transition: MobileTransition | null): string | undefined {
  if (transition === null) {
    return undefined;
  }
  const { direction, progress, source, target } = transition;
  if (source !== "inbox" && target !== "inbox") {
    const paneOffset = direction === 1 ? -50 * progress : -50 * (1 - progress);
    return `--mobile-pane-offset:${paneOffset}%`;
  }
  if (source === "inbox") {
    const paneOffset = direction * 100 * (1 - progress);
    const inboxOffset = -direction * 100 * progress;
    return `--mobile-pane-offset:${paneOffset}%;--mobile-inbox-offset:${inboxOffset}%`;
  }
  return `--mobile-pane-offset:${-direction * 100 * progress}%;--mobile-inbox-offset:0%`;
}

export default function App(): JSX.Element {
  let editorContainer!: HTMLDivElement;
  const compact = useCompactMode();
  const mobileVisualViewportStyle = createMobileVisualViewportStyle(compact);
  const mobileHistory = createMobileHistory(compact);
  const mobileSurface = mobileHistory.surface;
  const navigateMobileSurface = mobileHistory.select;
  const drillMobileSurface = mobileHistory.drill;
  const [mobileTransition, setMobileTransition] = createSignal<MobileTransition | null>(null);
  const activeBackendId = (): string => selectedSession()?.connection.id ?? LOCAL_BACKEND_ID;
  const localHost = () => hostConnection(LOCAL_BACKEND_ID)?.host;
  const publishMenuAction = (action: string, path?: string): boolean => {
    const host = localHost();
    if (host === undefined) {
      return false;
    }
    host.feature("window").publish("menu", path === undefined ? { action } : { action, path });
    return true;
  };
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
  // Compact surfaces are history entries, so the browser navigates them too (its own edge gesture, the OS
  // back button). Following the surface keeps the active pane right however it changed.
  createEffect(() => {
    const surface = mobileSurface();
    if (compact() && surface !== "inbox") {
      setActivePane(surface);
    }
  });
  const previewMobileSurface = (
    target: MobileSurface,
    direction: MobileSwipeDirection,
    progress: number,
  ): void => {
    if (!compact() || target === mobileSurface()) {
      return;
    }
    setMobileTransition({
      direction,
      navigation: "select",
      phase: "tracking",
      progress,
      source: mobileSurface(),
      target,
    });
  };
  const beginMobileBack = (): void => {
    const target = mobileHistory.backTarget();
    if (!compact() || target === null) {
      return;
    }
    setMobileTransition({
      direction: -1,
      navigation: "back",
      phase: "tracking",
      progress: 0,
      source: mobileSurface(),
      target,
    });
  };
  // Only the gesture's start opens a back transition; every later tick moves that one. A gesture whose
  // transition was dropped mid-swipe therefore commits nothing instead of re-deriving a second move.
  const previewMobileBack = (progress: number): void => {
    const transition = mobileTransition();
    if (
      transition === null ||
      transition.navigation !== "back" ||
      transition.phase !== "tracking"
    ) {
      return;
    }
    setMobileTransition({ ...transition, progress });
  };
  const commitMobileTransition = (transition: MobileTransition): void => {
    if (transition.navigation === "back") {
      mobileHistory.back();
    } else {
      navigateMobileSurface(transition.target);
    }
  };
  const settleMobileTransition = (commit: boolean): void => {
    const transition = mobileTransition();
    if (transition === null) {
      return;
    }
    const progress = commit ? 1 : 0;
    if (REDUCED_MOTION || transition.progress === progress) {
      if (commit) {
        commitMobileTransition(transition);
      }
      setMobileTransition(null);
      return;
    }
    setMobileTransition({
      ...transition,
      phase: commit ? "committing" : "canceling",
      progress,
    });
  };
  const finishMobileTransition = (event: TransitionEvent): void => {
    const transition = mobileTransition();
    if (
      transition === null ||
      transition.phase === "tracking" ||
      event.propertyName !== "transform" ||
      !(event.target instanceof Element) ||
      !event.target.matches(
        transition.source === "inbox" || transition.target === "inbox"
          ? ".pane-area"
          : ".layout-root",
      )
    ) {
      return;
    }
    if (transition.phase === "committing") {
      commitMobileTransition(transition);
    }
    setMobileTransition(null);
  };
  // A navigation Weavie didn't drive leaves a transition without the surface it was moving off, so it can
  // neither preview nor commit — dropping it is what keeps one browser gesture from landing two moves.
  createEffect(() => {
    const transition = mobileTransition();
    if (transition !== null && transition.source !== mobileSurface()) {
      setMobileTransition(null);
    }
  });
  const mobileBackSwipe = createMobileBackSwipe({
    canStart: () => compact() && mobileHistory.backTarget() !== null,
    onCancel: () => settleMobileTransition(false),
    onCommit: () => settleMobileTransition(true),
    onProgress: previewMobileBack,
    onStart: beginMobileBack,
  });
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
  const mobileSurfaceTitle = (surface: MobileSurface, label: string): string => {
    keybindingsVersion();
    if (surface === "inbox") {
      return `${label}${keyHint(CommandIds.showSessions)}`;
    }
    const shortcut = paneShortcut(numberOf(surface));
    return shortcut === "" ? label : `${label} (${shortcut})`;
  };
  // What LayoutView renders: in fullscreen, just the active pane (filling the pane area); the others collapse
  // to display:none but stay mounted, preserving their terminal/editor state. Switching panes re-points this,
  // keeping each pane fullscreen. Off ⇒ the real layout, never mutated by fullscreen.
  const displayRoot = createMemo<LayoutNode>(() => {
    if (compact()) {
      const transition = mobileTransition();
      if (transition !== null && transition.source !== "inbox" && transition.target !== "inbox") {
        const panes =
          transition.direction === 1
            ? [transition.source, transition.target]
            : [transition.target, transition.source];
        return {
          type: "split",
          dir: "row",
          weights: [1, 1],
          children: panes.map((kind) => ({ type: "pane", id: `compact-${kind}`, kind })),
        };
      }
      if (transition !== null) {
        const kind = transition.source === "inbox" ? transition.target : transition.source;
        return { type: "pane", id: "compact-transition", kind };
      }
      const surface = mobileSurface();
      const kind = surface === "inbox" ? (activePane() ?? AGENT_PANE_KIND) : surface;
      return { type: "pane", id: "compact", kind };
    }
    const kind = activePane();
    return fullscreen() && kind !== null ? { type: "pane", id: "fullscreen", kind } : layoutRoot();
  });
  createEffect(() => {
    const enabled = compact();
    setContext("compact", enabled);
    if (enabled) {
      dismissSplash();
    }
  });
  const sessionKey = (session: ClientSession): string =>
    `${session.connection.id}\0${session.address.slot}\0${session.address.incarnation}`;
  const terminalPaneKey = (session: ClientSession, pane: string): string =>
    `${sessionKey(session)}\0${pane}`;
  // Each loaded session's terminal panes register their focus fn here on mount; focusPane resolves the active
  // backend and session's entry. (The editor focuses via the controller directly.)
  const terminalFocus = new Map<string, () => void>();
  // The child-set terminal title (OSC 0/2), shown in the shell pane header (the agent pane keeps its fixed label).
  const [paneTitles, setPaneTitles] = createSignal<Record<string, string>>({});
  // Whether the Ctrl+N pane-switch hint badges are shown (the editor.paneShortcutHints setting; live-updated).
  const [showPaneHints, setShowPaneHints] = createSignal(currentEditorOptions().paneShortcutHints);

  // Stable backend/session keys for the active backend's loaded sessions, so <For> never remounts a session's
  // terminals across rail pushes — keeping them alive makes a switch pure show/hide. Excludes dormant and
  // other-backend sessions while ensuring a backend switch remounts even when both backends use the same id.
  const terminalSessions = createMemo<ClientSession[]>(() =>
    sessions().flatMap((session) =>
      session.loaded && session.backendId === activeBackendId() && session.owner !== null
        ? [session.owner]
        : [],
    ),
  );
  const agentTerminalSessions = createMemo<ClientSession[]>(() =>
    sessions().flatMap((session) =>
      session.loaded &&
      session.backendId === activeBackendId() &&
      (session.agentSurface === "terminal" || agentAuthenticationTerminalActive(session.owner)) &&
      session.owner !== null
        ? [session.owner]
        : [],
    ),
  );
  // The exact session whose panes are shown (null before the first rail push).
  const activeTermSession = selectedSession;
  const selectedCatalogSession = createMemo<RailSession | null>(() => {
    const selected = selectedSession();
    return selected === null
      ? null
      : (sessions().find((session) => session.owner === selected) ?? null);
  });
  const currentPullRequest = createMemo(() => {
    const status = pullRequestStatus(activeTermSession());
    return status !== null && status.branch === gitStatus()?.branch ? status.pullRequest : null;
  });
  createEffect(() => setContext("pullRequestAvailable", currentPullRequest() !== null));
  const activeAgentSurface = createMemo<"terminal" | "structured" | "unavailable" | null>(() => {
    return selectedCatalogSession()?.agentSurface ?? null;
  });
  const authenticationTerminalActive = createMemo(() =>
    agentAuthenticationTerminalActive(selectedSession()),
  );
  const agentTerminalVisible = createMemo(
    () => activeAgentSurface() === "terminal" || authenticationTerminalActive(),
  );
  // Transcript state belongs to the exact session and is projected before selection can reveal it.
  const selectedAgentPane = createMemo<AgentPaneModel | null>(() =>
    activeAgentSurface() === "structured" && !authenticationTerminalActive()
      ? agentPaneModel(selectedSession())
      : null,
  );
  const activeProviderId = createMemo<string | null>(
    () => selectedCatalogSession()?.providerId ?? null,
  );
  const activeAgentInputProtocol = createMemo(
    () => selectedCatalogSession()?.agentInputProtocol ?? 1,
  );

  // Desktop opens the shared Sessions surface as a modal; compact mode keeps it as native navigation.
  const [sessionsModalOpen, setSessionsModalOpen] = createSignal(false);
  const [openPrOpen, setOpenPrOpen] = createSignal(false);
  const [diffAgainstOpen, setDiffAgainstOpen] = createSignal(false);
  const sourceTokenPrompt = selectedSourceTokenPrompt;
  const [registerAgentOpen, setRegisterAgentOpen] = createSignal(false);
  const [acpRegistryOpen, setAcpRegistryOpen] = createSignal(false);
  const [acpRegistryBackendId, setAcpRegistryBackendId] = createSignal(LOCAL_BACKEND_ID);
  // The cloud panel's anchor (computed from the cloud button's rect) when open, else null.
  const [remotePanelAnchor, setRemotePanelAnchor] = createSignal<PopoverAnchor | null>(null);
  const openSessions = (): void => {
    setRemotePanelAnchor(null);
    if (compact()) {
      navigateMobileSurface("inbox");
    } else {
      setSessionsModalOpen(true);
    }
  };
  const closeSessions = (): void => {
    setSessionsModalOpen(false);
  };
  const openAcpRegistry = (backendId: string): void => {
    closeSessions();
    setAcpRegistryBackendId(backendId);
    setAcpRegistryOpen(true);
  };
  const sessionsModalActive = (): boolean => !compact() && sessionsModalOpen();
  createEffect(() => {
    if (compact()) {
      closeSessions();
    }
  });
  const dirListings = selectedDirectoryListings;
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
  setNotifySink(addToast, dismissKeyed);
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
  const fileIndex = (): string[] => selectedFileIndex().files;
  const indexRoot = (): string | null => selectedFileIndex().root;
  const indexPending = (): boolean => selectedFileIndex().pending;

  // The Monaco editor + all diff/review orchestration; App feeds it host messages and commands.
  const editor = createEditorController({
    onSaveError: (message) => addToast("error", message),
    onOpenError: (message) => addToast("warn", message),
    onCurrentFileChanged: setCurrentFile,
    onDestinationActivated: () => {
      if (compact()) {
        setActivePane("editor");
        drillMobileSurface("editor");
      }
    },
    focusVisibleOverlay: () => {
      setActivePane("editor");
      const overlay = editorContainer?.parentElement?.querySelector<HTMLElement>(
        ":scope > [data-kind='editor'][tabindex]",
      );
      overlay?.focus();
      return document.activeElement === overlay;
    },
    confirmDiscard,
    confirm,
    promptScratchName,
  });
  // Find-in-files results open through the editor controller (preview tab, cursor on the match's column).
  setSearchOpener((match, focus) => editor.openMatch(match.path, match.line, match.column, focus));

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
  // host has supplied its catalog (sessionsReceived) and there is no selected-session terminal, bring the
  // editor up so the shell still reveals. When terminals DO exist, their paint drives it (and reveals
  // before the editor eval), so this stays out of the way — it only fires when there is nothing to jam.
  createEffect(() => {
    if (sessionsReceived() && terminalSessions().length === 0) {
      startEditorOnce();
    }
  });

  const focusPane = (kind: string): void => {
    // Mark it active first: in fullscreen this synchronously makes its slot the visible one (the others are
    // display:none), so the focus call below lands on an on-screen element rather than a hidden one.
    setActivePane(kind);
    if (compact() && (kind === AGENT_PANE_KIND || kind === "terminal:shell" || kind === "editor")) {
      navigateMobileSurface(kind);
    }
    if (kind === "editor") {
      editor.focusEditor();
      return;
    }
    if (
      kind === AGENT_PANE_KIND &&
      activeAgentSurface() === "structured" &&
      !authenticationTerminalActive()
    ) {
      document.querySelector<HTMLTextAreaElement>(".agent-surface textarea")?.focus();
      return;
    }
    // Resolve the focusable xterm by the selected session id, so focus lands correctly regardless of
    // effect-flush timing on a switch.
    const pane = paneOf(kind);
    const session = activeTermSession();
    if (session !== null) {
      terminalFocus.get(terminalPaneKey(session, pane))?.();
    }
  };

  onCleanup(
    onSessionActivated(({ session, created }) => {
      if (!created) {
        return;
      }
      requestAnimationFrame(() => {
        if (selectedSession() === session) {
          focusPane(AGENT_PANE_KIND);
        }
      });
    }),
  );

  // Flip the active file between Source and Preview, only when its type can preview. Returns whether it acted,
  // so the command DECLINES (key falls through to the editor) on a non-previewable file.
  const toggleActivePreview = (): boolean => {
    const path = activePath();
    if (path === null || !canPreview(path)) {
      return false;
    }
    // Returning to Source hands focus back to Monaco; Preview preserves existing editor-pane focus on mount.
    if (toggleViewMode(path) === "source") {
      editor.focusEditor();
    }
    return true;
  };

  const activeTabBinding = createMemo<{
    session: ClientSession;
    path: string;
    kind: "file" | "web" | "source" | "plan";
  } | null>(() => {
    const session = selectedSession();
    if (session === null) {
      return null;
    }
    const path = activePathFor(session);
    if (path === null) {
      return null;
    }
    return {
      session,
      path,
      kind: openTabsFor(session).find((tab) => tab.path === path)?.kind ?? "file",
    };
  });

  // The active file's path when it's previewable, in Preview mode, and not under inline review (which owns the
  // editor) — drives the Preview overlay; null otherwise.
  const previewActivePath = createMemo<string | null>(() => {
    const binding = activeTabBinding();
    return binding !== null &&
      binding.kind === "file" &&
      canPreview(binding.path) &&
      isPreviewMode(binding.path) &&
      !editor.reviewActive()
      ? binding.path
      : null;
  });

  const activeMediaBinding = createMemo(() => {
    const binding = activeTabBinding();
    return binding !== null &&
      binding.kind === "file" &&
      mediaTypeOf(binding.path) !== null &&
      !editor.reviewActive()
      ? binding
      : null;
  });

  // The active tab's URL when it's a web (iframe) tab — drives the web overlay; null otherwise.
  const activeWebUrl = createMemo<string | null>(() => {
    const binding = activeTabBinding();
    return binding?.kind === "web" ? binding.path : null;
  });

  const activeSourceBinding = createMemo(() => {
    const binding = activeTabBinding();
    return binding?.kind === "source" ? binding : null;
  });

  const activePlanBinding = createMemo(() => {
    const binding = activeTabBinding();
    return binding?.kind === "plan" ? binding : null;
  });

  const resultAddress = (result: { data?: unknown }): { slot: string; incarnation: string } => {
    const address = (result.data as { address?: unknown } | undefined)?.address;
    if (
      address === null ||
      typeof address !== "object" ||
      typeof (address as { slot?: unknown }).slot !== "string" ||
      (address as { slot: string }).slot.length === 0 ||
      typeof (address as { incarnation?: unknown }).incarnation !== "string" ||
      (address as { incarnation: string }).incarnation.length === 0
    ) {
      throw new Error("The session operation did not return an exact live address.");
    }
    return address as { slot: string; incarnation: string };
  };

  const createSessionAt = (
    backendId: string,
    args: {
      branch?: string;
      base: "source" | "main";
      existing: boolean;
      prompt?: string;
      attachments?: { id: string; mime: string; dataB64: string }[];
      agentProviderId: string;
    },
  ): Promise<boolean> => {
    const selected = selectedSession();
    const source =
      !args.existing && args.base === "source" && selected?.connection.id === backendId
        ? selected.address
        : undefined;
    return dispatchCommandFromCatalog(backendId, CommandIds.newSession, { ...args, source })
      .then((result) => {
        if (!result.ok) {
          throw new Error(result.error ?? "The session could not be created.");
        }
        if (compact()) {
          navigateMobileSurface(AGENT_PANE_KIND);
        } else {
          closeSessions();
        }
        return true;
      })
      .catch((error: unknown) => {
        addToast("error", error instanceof Error ? error.message : String(error));
        return false;
      });
  };

  const openPullRequestAt = (
    backendId: string,
    target: { number: number; owner: string; repo: string },
  ): void => {
    const toastKey = `open-pr:${target.number}`;
    const selected = selectedSession();
    const session = selected?.connection.id === backendId ? selected : undefined;
    if (session === undefined) {
      addToast("error", `No live session is available on ${backendLabel(backendId)}.`, toastKey);
      return;
    }
    const commit = beginClientSelection();
    void session
      .feature("pullRequests")
      .request<
        {
          ok: boolean;
          error?: string;
          data?: unknown;
        },
        typeof target
      >("open", target)
      .then(async (result) => {
        if (!result.ok) {
          throw new Error(result.error ?? "The pull request could not be opened.");
        }
        await applySessionActivation(backendId, result, commit);
      })
      .then(() => dismissKeyed(toastKey))
      .catch((error: unknown) =>
        addToast("warn", error instanceof Error ? error.message : String(error), toastKey),
      );
  };

  const switchToSession = (session: RailSession): Promise<boolean> => {
    // A backend whose link is down can't serve the switch — refuse loudly at the click rather than paint
    // the optimistic highlight and queue a frame that would replay as a stale navigation on reconnect.
    if (backendPhase(session.backendId) !== "online") {
      addToast(
        "error",
        `Can't switch to ${session.label} — ${backendLabel(session.backendId)} isn't reachable right now (reconnecting…).`,
        `switch-offline:${session.backendId}`,
      );
      return Promise.resolve(false);
    }
    const commit = beginClientSelection();
    beginSessionSelection(session.backendId, session.id);
    flushEditorSession();
    return editor
      .flushDirty()
      .then(async () => {
        let target = clientSession(session.backendId, session.id);
        if (target === undefined) {
          const loaded = await invokeSessionCommandOnBackend(
            session.backendId,
            CommandIds.loadSession,
            {
              id: session.id,
            },
          );
          if (!loaded.ok) {
            throw new Error(loaded.error ?? `Couldn't load ${session.label}.`);
          }
          target = await waitForClientSession(session.backendId, resultAddress(loaded));
        }
        commit(target);
      })
      .then(() => {
        closeSessions();
        if (compact()) {
          navigateMobileSurface(AGENT_PANE_KIND);
        }
        return true;
      })
      .catch((error: unknown) => {
        addToast("error", error instanceof Error ? error.message : String(error));
        return false;
      });
  };

  const openSession = (session: RailSession): Promise<boolean> => {
    if (!session.active) {
      return switchToSession(session);
    }
    closeSessions();
    if (compact()) {
      navigateMobileSurface(AGENT_PANE_KIND);
    }
    return Promise.resolve(true);
  };

  // A backend's human name for connection messages ("the host" for the local headless link).
  const backendLabel = (id: string): string =>
    id === LOCAL_BACKEND_ID ? "the host" : backendName(id);
  // The active backend's human name for the reconnecting banner.
  const connectionLabel = (): string => backendLabel(activeBackendId());

  // The location to preselect in the New Session prompt: the last-used backend if still connected, else
  // local (a remembered agent that failed to reconnect falls back rather than picking a dead id).
  const defaultLocation = (): string => {
    const last = lastLocation();
    return connectedBackends().some((b) => b.id === last) ? last : "local";
  };

  // Switch to the next/prev LOADED rail chip stepRailTarget picks (dormant and unreachable chips skipped);
  // false falls the keystroke through when there's nothing to move to.
  const stepSession = (delta: number): boolean => {
    const target = stepRailTarget(
      railSessions().filter((s) => s.loaded && !s.offline),
      delta,
    );
    if (target === null) {
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
    removesCheckout: boolean;
    state: DeleteSessionState;
    changedFiles: string[];
    changedCount: number;
    backendId: string;
  } | null>(null);
  // Interactive delete (rail menu / cloud panel / palette): no args targets the selected session. Classify the
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
          removesCheckout?: boolean;
          changedFiles: string[];
          changedCount: number;
        }
      | undefined;
    const changedFiles = info?.changedFiles ?? [];
    setDeleteReq({
      id,
      label: info?.label ?? id,
      removesCheckout: info?.removesCheckout === true,
      state: info?.state ?? "clean",
      changedFiles,
      changedCount: info?.changedCount ?? changedFiles.length,
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
    if (!result.ok) {
      addToast("warn", result.error ?? "Couldn't delete the session.");
    }
  };

  // Persist the layout after a user gesture (debounced). Skipped until the host's initial layout push, so we
  // never overwrite the saved state with the default before it loads.
  const persistTimers = new Map<string, number>();
  const persistRoot = (root: LayoutNode): void => {
    const backendId = activeBackendId();
    const base = layoutDocument();
    if (base === null) {
      return;
    }
    window.clearTimeout(persistTimers.get(backendId));
    persistTimers.set(
      backendId,
      window.setTimeout(() => {
        persistTimers.delete(backendId);
        sendLayout(backendId, { ...base, root });
      }, 400),
    );
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

  // Renders each stable pane slot. Agent terminals stay mounted; structured sessions share one selected tree.
  const openTerminalContextMenu = (event: MouseEvent, url: string | undefined): void => {
    const entries: ContextMenuEntry[] = [];
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
  };

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
            session={selectedSession}
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
                    { commandId: CommandIds.editorPeekDefinition },
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
            {/* Preview mode: render the active file over the still-mounted Monaco host. */}
            <Show when={previewActivePath() !== null}>
              <Suspense>
                <PreviewPane
                  path={() => previewActivePath() as string}
                  content={() => editor.activeContent()}
                  focusOnMount={focusedKind() === "editor"}
                />
              </Suspense>
            </Show>
            {/* A media (image/video) file tab: render it over the still-mounted Monaco host. */}
            <Show when={activeMediaBinding()} keyed>
              {(binding) => (
                <MediaPane
                  session={binding.session}
                  path={binding.path}
                  focusOnMount={focusedKind() === "editor"}
                />
              )}
            </Show>
            {/* A web tab: render its URL in an iframe over the still-mounted Monaco host. */}
            <Show when={activeWebUrl() !== null}>
              <WebTabPane url={() => activeWebUrl() as string} />
            </Show>
            {/* A source tab: render the fetched Notion doc as rich HTML in a shadow root over Monaco (or its
                loading spinner / fetch error while it resolves). */}
            <Show when={activeSourceBinding()} keyed>
              {(binding) => (
                <Suspense>
                  <SourceView
                    doc={() => sourceDoc(binding.session, binding.path)}
                    session={binding.session}
                    target={() => binding.path}
                    focusOnMount={focusedKind() === "editor"}
                  />
                </Suspense>
              )}
            </Show>
            {/* A completed agent plan: host-owned Markdown in a read-only virtual document. */}
            <Show when={activePlanBinding()} keyed>
              {(binding) => (
                <Suspense>
                  <PlanView
                    session={binding.session}
                    path={binding.path}
                    focusOnMount={focusedKind() === "editor"}
                  />
                </Suspense>
              )}
            </Show>
          </div>
          <EditorFooter
            onOpenRecent={(path) => editor.openFile(path, 1)}
            root={() => indexRoot() ?? ""}
          />
        </div>
      );
    }
    if (kind === AGENT_PANE_KIND) {
      const pane = paneOf(kind);
      return (
        <div class="agent-slot-stack">
          <Show when={selectedAgentPane()} keyed>
            {(model) => (
              <AgentPane
                backendId={selectedCatalogSession()?.backendId ?? LOCAL_BACKEND_ID}
                compact={compact()}
                inputProtocol={activeAgentInputProtocol()}
                model={model}
                providerId={activeProviderId()}
                active={focusedKind() === AGENT_PANE_KIND}
                shortcut={paneShortcut(numberOf(kind))}
                onFocus={() => {
                  if (!compact() || mobileSurface() === kind) {
                    focusPane(kind);
                  }
                }}
              />
            )}
          </Show>
          <div
            class="terminal-surface agent-terminal-surface"
            classList={{
              active: agentTerminalVisible() && focusedKind() === AGENT_PANE_KIND,
              hidden: !agentTerminalVisible(),
            }}
            data-kind={kind}
            data-surface="terminal"
          >
            <div
              class="pane-head"
              role="toolbar"
              onMouseDown={(event) => {
                event.preventDefault();
                focusPane(kind);
              }}
            >
              <span class="pane-label">
                {authenticationTerminalActive() ? "Agent sign in" : "Claude Code"}
              </span>
              <Show when={showPaneHints() && paneShortcut(numberOf(kind)) !== ""}>
                <span class="pane-shortcut">{paneShortcut(numberOf(kind))}</span>
              </Show>
            </div>
            <div class="pane-body">
              <For each={agentTerminalSessions()}>
                {(session) => {
                  const paneKey = terminalPaneKey(session, pane);
                  const selected = (): boolean => selectedSession() === session;
                  onCleanup(() => terminalFocus.delete(paneKey));
                  return (
                    <div class="term-host" classList={{ hidden: !selected() }}>
                      <TerminalView
                        session={session}
                        pane={pane}
                        active={selected() && agentTerminalVisible()}
                        onFirstRender={() => {
                          dismissSplash();
                          startEditorOnce();
                        }}
                        onFocusReady={(focus) => terminalFocus.set(paneKey, focus)}
                        onTitle={(title) =>
                          setPaneTitles((prev) => ({ ...prev, [paneKey]: title }))
                        }
                        onContextMenu={openTerminalContextMenu}
                      />
                    </div>
                  );
                }}
              </For>
            </div>
          </div>
        </div>
      );
    }
    const pane = paneOf(kind);
    // The shell pane shows the child-set title (cwd / running command) when it has one.
    const paneTitle = (): string => {
      const session = activeTermSession();
      const title = session === null ? undefined : paneTitles()[terminalPaneKey(session, pane)];
      return title !== undefined && title.length > 0 ? title : "Terminal";
    };
    const paneSessions = terminalSessions;
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
          {/* One live xterm per exact session incarnation, only the selected owner shown. */}
          <For each={paneSessions()}>
            {(session) => {
              const paneKey = terminalPaneKey(session, pane);
              const isActive = (): boolean => selectedSession() === session;
              onCleanup(() => terminalFocus.delete(paneKey));
              return (
                <div class="term-host" classList={{ hidden: !isActive() }}>
                  <TerminalView
                    session={session}
                    pane={pane}
                    active={isActive()}
                    onFirstRender={() => {
                      dismissSplash();
                      startEditorOnce();
                    }}
                    onFocusReady={(focus) => terminalFocus.set(paneKey, focus)}
                    onTitle={(title) => setPaneTitles((prev) => ({ ...prev, [paneKey]: title }))}
                    onContextMenu={openTerminalContextMenu}
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

  // When the browser is open and the selected session's root listing hasn't loaded, request it. Keyed on
  // indexRoot(), so the browser follows client selection.
  createEffect(() => {
    const root = indexRoot();
    if (browserOpen() && root !== null && dirListings()[root] === undefined) {
      listSelectedDirectory(root);
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

    const offViewBinding = registerViewFeature((session) => {
      const cleanups = [
        session.feature("view").on<{ kind: string }>("focusPane", ({ kind }) => {
          const active = document.activeElement;
          const typingInOverlay =
            active instanceof HTMLElement &&
            !active.classList.contains("xterm-helper-textarea") &&
            (active.tagName === "INPUT" || active.tagName === "TEXTAREA");
          if (!typingInOverlay) {
            focusPane(kind);
          }
        }),
        session
          .feature("view")
          .on<{ query: string; line: number }>("focusOmnibar", ({ query, line }) =>
            focusOmnibarFileSearch(query, line),
          ),
      ];
      return () => {
        for (const cleanup of cleanups) {
          cleanup();
        }
      };
    });
    const offSourceErrors = onSourceEditError((error) => {
      const shown =
        error.session === selectedSession() &&
        (activeSourceEditor()?.showSaveError(error.target, error.message, error.stale) ?? false);
      if (!shown) {
        addToast("error", `Notion edit failed: ${error.message}`);
      }
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
      registerCommand(CommandIds.toggleAgentMermaidPreview, () => toggleActiveAgentMermaid()),
      registerCommand(CommandIds.toggleFileBrowser, () => toggleBrowser()),
      // Terminal copy/paste (act on the focused xterm, clipboard via the host); gated terminalFocused.
      installTerminalClipboardCommands(),
      registerCommand(CommandIds.focusOmnibarFiles, () => focusOmnibar("file")),
      registerCommand(CommandIds.focusOmnibarCommands, () => focusOmnibar("command")),
      registerCommand(CommandIds.goToSymbol, () => focusOmnibar("docSymbol")),
      registerCommand(CommandIds.goToWorkspaceSymbol, () => focusOmnibar("wsSymbol")),
      // Find in Files (Ctrl+Shift+F / palette): open the content-search panel seeded from the editor selection
      // (re-invoking while open re-seeds + refocuses the input).
      registerCommand(CommandIds.findInFiles, () => {
        seedSearch(editor.selectionText());
        setSearchOpen(true);
      }),
      // The panel's option toggles (searchPanelFocused-gated chords; visible-panel-gated here so a palette run
      // with the panel closed falls through instead of flipping hidden state).
      registerCommand(CommandIds.searchToggleMatchCase, () =>
        searchOpen() ? toggleSearchOption("caseSensitive") : false,
      ),
      registerCommand(CommandIds.searchToggleWholeWord, () =>
        searchOpen() ? toggleSearchOption("wholeWord") : false,
      ),
      registerCommand(CommandIds.searchToggleRegex, () =>
        searchOpen() ? toggleSearchOption("regex") : false,
      ),
      registerCommand(CommandIds.searchToggleGitignore, () =>
        searchOpen() ? toggleSearchOption("excludeGitignored") : false,
      ),
      // F4 / Shift+F4 step the last search's results from anywhere; they decline (fall through) with none.
      registerCommand(CommandIds.searchNextResult, () => stepSearchResult(1)),
      registerCommand(CommandIds.searchPrevResult, () => stepSearchResult(-1)),

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
      // Blame: opens the popover on the cursor's line, or says why that line has no commit behind it. Declines
      // only with no editor mounted, so the palette entry never looks like it silently did nothing.
      registerCommand(CommandIds.showBlame, () => editor.showBlameAtCursor()),
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
      registerCommand(CommandIds.editorPeekDefinition, () =>
        editor.triggerAction("editor.action.peekDefinition"),
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
      registerCommand(CommandIds.runTestAtCursor, async () => {
        await (await import("./tests/test-lens")).runTestAtCursor();
      }),
      // Workspace/window menu commands always target the page-serving host, even while a remote session is active.
      registerCommand(CommandIds.openFolder, () =>
        NATIVE_SHELL ? publishMenuAction("open-folder") : false,
      ),
      registerCommand(CommandIds.openRecentWorkspace, (args) => {
        if (!NATIVE_SHELL) {
          return false;
        }
        const path = (args as { path?: unknown } | undefined)?.path;
        return typeof path === "string" && path.length > 0
          ? publishMenuAction("open-recent", path)
          : false;
      }),
      registerCommand(CommandIds.closeWindow, () =>
        NATIVE_SHELL ? publishMenuAction("close-window") : false,
      ),
      registerCommand(CommandIds.exit, () => (NATIVE_SHELL ? publishMenuAction("exit") : false)),
      // Open URL: a `url` arg (the terminal's "Open in Weavie" menu / Claude) opens it in a web tab directly;
      // no arg (the palette / $mod+O) prompts. "Open in Browser" opens the same URL in the OS browser instead.
      registerCommand(CommandIds.openUrl, (args) => {
        const url = (args as { url?: unknown } | undefined)?.url;
        if (typeof url === "string" && url.length > 0) {
          openSelectedSourceTarget(url);
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
      // Sessions (Ctrl+Shift+N / palette / the rail's "+"): modal on desktop, native surface on mobile.
      registerCommand(CommandIds.showSessions, openSessions),
      registerCommand(CommandIds.manageAcpAgents, () => openAcpRegistry(activeBackendId())),
      // Open Pull Request… (Ctrl+Shift+R / palette): pick a PR to check out as a session.
      registerCommand(CommandIds.openPr, () => setOpenPrOpen(true)),
      registerCommand(CommandIds.openCurrentPr, () => {
        const pullRequest = currentPullRequest();
        if (pullRequest === null) {
          return false;
        }
        openUrlExternal(pullRequest.url);
      }),
      // Diff Against… (Ctrl+Shift+D / palette): review the working tree against a ref. A 'ref' arg (Claude /
      // a keybinding) skips the prompt; the helpers are the same flow with their ref fixed.
      registerCommand(CommandIds.diffAgainst, (args) => {
        const ref = (args as { ref?: unknown } | undefined)?.ref;
        if (typeof ref === "string" && ref.trim().length > 0) {
          selectedSession()?.feature("review").publish("diffAgainst", { reference: ref.trim() });
        } else {
          setDiffAgainstOpen(true);
        }
        return true;
      }),
      registerCommand(CommandIds.diffAgainstParent, () => {
        selectedSession()?.feature("review").publish("diffAgainst", { reference: "HEAD^" });
        return true;
      }),
      registerCommand(CommandIds.diffAgainstHead, () => {
        selectedSession()?.feature("review").publish("diffAgainst", { reference: "HEAD" });
        return true;
      }),
      // Next / Previous Session (Ctrl+Tab / Ctrl+Shift+Tab, gated !editorFocused so the editor's own Ctrl+Tab
      // still cycles tabs): cycle the rail, wrapping. stepSession returns false only when there's no chip to move
      // to (empty rail, or a lone active chip), so the chord falls through.
      registerCommand(CommandIds.nextSession, () => stepSession(1)),
      registerCommand(CommandIds.prevSession, () => stepSession(-1)),
      // Focus Session (programmatic; the notification click-through): bring a session to the foreground by
      // 'id' (+ optional 'backendId' and exact 'incarnation'). Declines an unknown or stale session.
      registerCommand(CommandIds.focusSession, (args) => {
        const a = args as { id?: unknown; backendId?: unknown; incarnation?: unknown } | undefined;
        if (typeof a?.id !== "string" || a.id.length === 0) {
          return false;
        }
        const backendId =
          typeof a.backendId === "string" && a.backendId.length > 0
            ? a.backendId
            : LOCAL_BACKEND_ID;
        const target = findSession(backendId, a.id);
        if (
          target === undefined ||
          (typeof a.incarnation === "string" &&
            (a.incarnation.length === 0 || target.owner?.address.incarnation !== a.incarnation))
        ) {
          return false;
        }
        void openSession(target);
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
        void openSession(target);
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
    const offAutoscroll = installMiddleClickAutoscroll();

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
      for (const timer of persistTimers.values()) {
        window.clearTimeout(timer);
      }
      offEditorOptions();
      offKeybindings();
      offDoubleShift();
      offAutoscroll();
      for (const off of offCommands) {
        off();
      }
      document.removeEventListener("focusin", onFocusIn);
      offSourceErrors();
      offViewBinding();
      editor.dispose();
    });
  });

  return (
    <div
      class="app"
      classList={{
        compact: compact(),
        "mobile-inbox": compact() && mobileSurface() === "inbox",
        "mobile-transition": mobileTransition() !== null,
        "mobile-transition-from-inbox": mobileTransition()?.source === "inbox",
        "mobile-transition-settling":
          mobileTransition() !== null && mobileTransition()?.phase !== "tracking",
        "mobile-transition-to-inbox": mobileTransition()?.target === "inbox",
        "mobile-transition-two-panes":
          mobileTransition() !== null &&
          mobileTransition()?.source !== "inbox" &&
          mobileTransition()?.target !== "inbox",
      }}
      style={`${mobileVisualViewportStyle()}${mobileTransitionStyle(mobileTransition()) ?? ""}`}
      onTransitionEnd={finishMobileTransition}
    >
      <Show when={CUSTOM_TITLEBAR}>
        <TitleBar
          maximized={windowMaximized()}
          focused={hostWindowFocused()}
          files={fileIndex()}
          filesPending={indexPending()}
          root={indexRoot()}
          currentFile={currentFile()}
          onWindowControl={(action) =>
            localHost()?.feature("window").publish("control", { action })
          }
          onOpenFile={(path, line) => revealSelectedFile(path, line)}
          onRequestIndex={refreshSelectedFileIndex}
          symbols={editor.symbols}
        />
      </Show>
      <Show when={CUSTOM_TITLEBAR}>
        <ResizeFrame maximized={windowMaximized()} />
      </Show>
      <Show when={MAC_TITLEBAR || LINUX_TITLEBAR}>
        <NativeTitleBar
          platform={LINUX_TITLEBAR ? "linux" : "mac"}
          files={fileIndex()}
          filesPending={indexPending()}
          root={indexRoot()}
          currentFile={currentFile()}
          workspaceLabel={SHELL?.workspaceLabel ?? "weavie"}
          recents={SHELL?.recents ?? []}
          onOpenFile={(path, line) => revealSelectedFile(path, line)}
          onRequestIndex={refreshSelectedFileIndex}
          symbols={editor.symbols}
        />
      </Show>
      <div class="app-body">
        <SessionRail
          sessions={railSessions()}
          inert={sessionsModalActive()}
          hasRemotes={remoteAgentRows().length > 0}
          remoteActive={remoteActivity()}
          onSwitch={openSession}
          onNew={openSessions}
          onToggleRemotes={(rect) =>
            setRemotePanelAnchor((open) =>
              open !== null
                ? null
                : {
                    left: rect.left,
                    right: rect.right,
                    top: rect.top,
                    bottom: rect.bottom,
                  },
            )
          }
        />
        <MobileWorkspace
          surface={mobileSurface()}
          compact={compact()}
          modalOpen={sessionsModalActive()}
          inboxActive={compact() ? mobileSurface() === "inbox" : sessionsModalOpen()}
          sessions={sessions()}
          initialBackendId={defaultLocation()}
          initialProviderId={defaultAgentProvider(defaultLocation())}
          onOpen={openSession}
          onCreate={(seed, backendId, providerId) => {
            setLastLocation(backendId);
            setDefaultAgentProvider(backendId, providerId);
            promoteNextSessionOn(backendId);
            return createSessionAt(backendId, {
              branch: seed.branch,
              base: seed.base,
              existing: seed.existing,
              prompt: seed.prompt,
              attachments: seed.attachments,
              agentProviderId: providerId,
            });
          }}
          onManageAcp={openAcpRegistry}
          surfaceTitle={mobileSurfaceTitle}
          onDismiss={closeSessions}
          onSurface={navigateMobileSurface}
          onSwipeCancel={() => settleMobileTransition(false)}
          onSwipeCommit={() => settleMobileTransition(true)}
          onSwipeProgress={previewMobileSurface}
        />
        <div
          class="pane-area"
          inert={sessionsModalActive()}
          classList={{
            offline: activeBackendOffline(),
          }}
          on:touchstart={mobileBackSwipe.onTouchStart}
          on:touchmove={{ handleEvent: mobileBackSwipe.onTouchMove, passive: false }}
          on:touchend={mobileBackSwipe.onTouchEnd}
          on:touchcancel={mobileBackSwipe.onTouchCancel}
        >
          <LayoutView root={displayRoot()} renderPane={renderPane} onResize={onLayoutResize} />
          <Show when={fullscreen() && !compact()}>
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
          <Show when={!activeBackendOffline() && activeBackendBuildMismatch()}>
            {(mismatch) => (
              <output class="connection-banner connection-banner-error" role="alert">
                <span>
                  {connectionLabel()} runs build {mismatch().backend} — this client is{" "}
                  {mismatch().client}. Sessions there won't work until both run the same build.
                </span>
              </output>
            )}
          </Show>
        </div>
      </div>
      <Show when={openPrOpen()}>
        <OpenPrPrompt
          backendId={defaultLocation()}
          onOpen={(target, location) => {
            setOpenPrOpen(false);
            setLastLocation(location);
            // The fetch→checkout→seed request renders nothing for seconds; its correlated result clears this
            // spinner after selecting the exact new session, or replaces it with a keyed warning on failure.
            addToast("busy", `Opening PR #${target.number}…`, `open-pr:${target.number}`);
            // Promote + bind the backend before opening, same order as New Session, so the worktree-checkout
            // reply wires the panes to it; the host resolves the PR's branch refs by number, then checks it out.
            promoteNextSessionOn(location);
            openPullRequestAt(location, target);
          }}
          onCancel={() => setOpenPrOpen(false)}
        />
      </Show>
      <Show when={diffAgainstOpen()}>
        <DiffAgainstPrompt
          onPick={(ref) => {
            setDiffAgainstOpen(false);
            selectedSession()?.feature("review").publish("diffAgainst", { reference: ref });
          }}
          onCancel={() => setDiffAgainstOpen(false)}
        />
      </Show>
      <Show when={sourceTokenPrompt()}>
        {(prompt) => (
          <SourceTokenPrompt
            session={prompt().session}
            sourceId={prompt().sourceId}
            label={prompt().label}
            onClose={() => dismissSourceTokenPrompt(prompt().session)}
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
            openSessions();
          }}
        />
      </Show>
      <Show when={acpRegistryOpen()}>
        <AcpRegistryModal
          backendId={acpRegistryBackendId()}
          onClose={() => setAcpRegistryOpen(false)}
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
            onExpand={listSelectedDirectory}
            onOpen={(path) => revealSelectedFile(path, 1)}
            onClose={() => setBrowserOpen(false)}
          />
        </Suspense>
      </Show>
      <Show when={searchOpen()}>
        <Suspense>
          <SearchPanel
            onClose={() => {
              setSearchOpen(false);
              editor.focusEditor();
            }}
          />
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
        onDismiss={(id, forever) =>
          selectedSession()
            ?.connection.host.feature("suggestions")
            .publish("dismiss", { id, forever })
        }
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
            openSelectedSourceTarget(url);
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
            removesCheckout={req().removesCheckout}
            state={req().state}
            changedFiles={req().changedFiles}
            changedCount={req().changedCount}
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
      {/* The blame popover for a clicked line annotation. Keyed so picking another line rebuilds it against
          the new target rather than leaving the previous commit's resources in place. */}
      <Show when={blameTarget()} keyed>
        {(target) => <BlamePopover target={target} />}
      </Show>
    </div>
  );
}
