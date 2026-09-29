// Owns the Monaco editor lifecycle and diff/review orchestration on App's behalf. Drives the editor host +
// inline-diff layer (editor-host.ts / inline-diff.ts).

import type * as monaco from "monaco-editor";
import { createSignal } from "solid-js";
import {
  type ClientSession,
  isBrowserHostedShell,
  log,
  onSelectedSession,
  registerSessionFeature,
  selectedSession,
} from "../bridge";
import type { FocusIntent, InteractionIntent } from "../chrome/interaction-intent";
import { focusOmnibarFileSearch } from "../chrome/omnibar-controller";
import type { CommandResult } from "../commands/types";
import { ownFileNavigation } from "../files/reveal";
import { PAGE_EPOCH } from "../messaging/page-epoch";
import { dismissSplash } from "../splash";
import { mark } from "../startup-timing";
// Type-only (erased at build): the symbol query surface's monaco glue is dynamically imported in start(), so it
// stays in the lazily loaded editor chunk rather than the first-paint entry chunk.
import type { SymbolActions } from "../symbols/symbol-match";
import type { CommentProse } from "./comment-prose";
import {
  editorContexts,
  type TextEditorConnection,
  type TextEditorMenuHandler,
} from "./editor-context";
import type { EditorHost } from "./editor-host";
import { createEditorNavigation } from "./editor-navigation";
import { createEditorSymbols, noEditorSymbols } from "./editor-symbols";
import {
  createFileNavigation,
  type FileOpenCommit,
  type ResolvedFileOpen,
} from "./file-navigation";
import { samePath } from "./fs-path";
import type {
  HunkUnkeep,
  InlineDiff,
  InlineDiffOptions,
  ReviewHistoryHandlers,
  ReviewScopeState,
} from "./inline-diff";
import type { NavLocation, TextLocation } from "./nav-history";
import type { ReviewCommentDrafts, ReviewCommentPost } from "./review/review-comment-drafts";
import { ReviewCommentSession } from "./review/review-comment-session";
import {
  applyReviewDecision,
  type ReviewDecision,
  type ReviewDecisionCompletion,
} from "./review/review-decision";
import { reviewHistoryHandlers } from "./review/review-history-handlers";
import { createTabActions, type TabActions } from "./tab-actions";
import { isFileTab, REVIEW_TAB_KEY, tabKind } from "./tab-entry";
import { focusTabContent, type TabOwner, type TabPresenter } from "./tab-owner";

export type { TabActions } from "./tab-actions";

import { removeAgentPlan, setAgentPlan } from "./plan/plan-store";
import { REVEAL_SCROLL } from "./reveal-scroll";
import {
  canCloseReview,
  createReviewStore,
  type ReviewComments,
  type ReviewFile,
  type ReviewFileDiff,
  type ReviewHistory,
  type ReviewOverview,
} from "./review/review-store";
import type { ReviseMarks, ReviseRegion } from "./revise-marks";
import {
  type ActivateResult,
  activateTabFor,
  activePathFor,
  activeTabFor,
  captureReviewFor,
  captureViewState,
  closeTabFor,
  convertScratchFor,
  dropReviewTabFor,
  flushEditorSessionFor,
  onEditorSessionChanged,
  openTabFor,
  openTabsFor,
  snapshotEditorSessionFor,
  tabOwnerFor,
} from "./session-store";
import type { EditorSession } from "./session-types";
import { SESSION_FILE_SCHEME, sessionOwnsUri, sessionUriHostPath } from "./session-uri-owner";

// Only a genuine hang trips this, never a slow cold start: the editor chunk (~750KB of Monaco + workers) plus
// vscode-services init can legitimately run tens of seconds on a loaded machine or across the remote worker hop
// (browser → Runner → worker). 15s misfired there — a slow-but-successful boot got killed and `data-ready` never
// stamped — so it's set well above any real cold start while still bounding an init that truly never settles.
const EDITOR_INIT_MS = 60_000;

class FileOpenError extends Error {
  constructor() {
    super("The file could not be opened.");
  }
}

export interface EditorControllerDeps {
  interaction: InteractionIntent;
  /** Surface a debounced save that failed to reach disk. */
  onSaveError: (message: string) => void;
  /** Surface a file that couldn't be opened (read), so a failed open errors loudly instead of a blank tab. */
  onOpenError: (message: string) => void;
  /** Report the file the editor is showing so the browser / title bar can track it. */
  onCurrentFileChanged: (path: string | null) => void;
  /** Reveal an accepted foreground editor destination in the app's active presentation. */
  onDestinationActivated: () => void;
  /** Present a menu for the exact editor connection that received the context-menu event. */
  onEditorContextMenu: TextEditorMenuHandler;
  /** Confirm discarding unsaved scratch buffers about to be closed (`names`); the single close-path guard. */
  confirmDiscard: (names: string[]) => Promise<boolean>;
  /** Confirm a destructive review action (Revert file / Revert all). Resolves true to proceed. */
  confirm: (options: { title: string; body: string; confirmLabel: string }) => Promise<boolean>;
  /** Prompt in-app for a scratch buffer's save name on a browser-served host (no native Save-As dialog);
   * resolves the chosen workspace-relative name, or null if cancelled. */
  promptScratchName: (suggestedName: string) => Promise<string | null>;
  /** Ask what to do to the selected lines; resolves the instruction, or null if cancelled. */
  promptRevision: (lineCount: number) => Promise<string | null>;
}

/**
 * Why the editor pane is being given a destination — the wire value the host stamps on every open it pushes.
 * Navigation focuses the editor pane; passive reveal and restoration preserve the current pane.
 */
type EditorOpenIntent = "navigation" | "reveal" | "restore";

interface DiffProposal {
  id: string;
  path: string;
  tabName: string;
  original: string;
  proposed: string;
}

interface SessionProposal extends DiffProposal {
  addedTab: boolean;
  priorActive: string | null;
}

/** Back/forward navigation through visited editor locations, exposed to the Go Back / Go Forward commands. */
export interface NavActions {
  /** Go to the previous location; false when there's nothing behind (so the keybinding falls through). */
  back(session: ClientSession): boolean;
  /** Go to the next location; false when there's nothing ahead. */
  forward(session: ClientSession): boolean;
  /** Whether a previous location is available (reactive). */
  canBack(): boolean;
  /** Whether a next location is available (reactive). */
  canForward(): boolean;
}

export interface EditorController {
  /** Revise the selected lines: prompt for an instruction, then hand the region to the host. */
  reviseSelection(connection: TextEditorConnection, selection: monaco.Selection): void;
  /** Loads the editor chunk and brings up the editor in `container`; fades the splash when settled. */
  start(container: HTMLElement): void;
  /**
   * Opens a file (preview tab when `preview`), replaying once the editor chunk has loaded (last wins).
   * `line` reveals that line; `undefined` means no target, so an already-open tab keeps the user's position.
   */
  openFile(path: string, line: number | undefined, preview?: boolean): void;
  /** Opens an http(s) URL as a web (iframe) tab in the editor tab strip. */
  openWebTab(url: string): void;
  /** Opens a fetched source doc (Notion) as a source (shadow-root) tab in the editor tab strip, keyed by its target. */
  openSourceTab(target: string): void;
  /** Focuses the editor (for focus-pane). */
  focusEditor(): void;
  /**
   * Opens a find-in-files hit in the preview tab, landing the cursor at line:column. `focus: false` reveals
   * without stealing focus — the panel's live preview while arrowing through results.
   */
  openMatch(path: string, line: number, column: number, focus: boolean): void;
  /** New File: asks the host to create a scratch buffer, which comes back as an open-file with `scratch`. */
  newFile(session: ClientSession): void;
  /** Save the active editor: a scratch buffer prompts for a name; a real file is already autosaved. */
  save(tab: TabOwner): boolean;
  /**
   * Flushes every dirty working copy to the active backend and resolves once they land — called before a
   * cross-backend session switch so edits persist on their own host. Resolves immediately when unmounted.
   */
  flushDirty(): Promise<void>;
  /** Flushes one exact session's editor state and dirty working copies before its backend is torn down. */
  flushSession(session: ClientSession): Promise<void>;
  /** Open the review overview, or a specific file when a path is supplied. */
  openReview(session: ClientSession, path: string | undefined, line: number | undefined): boolean;
  /** The active file's current working-copy text (reactive), for the Preview overlay; "" when none. */
  activeContent(): string;
  /** Whether an inline openDiff review is showing (reactive), so Preview suspends rather than hiding it. */
  reviewActive(): boolean;
  /** How many files are pending post-turn review (reactive), so the empty-state pane can surface a review cue
   * when no file is open. */
  parkedReviewCount(): number;
  readonly hostReady: Promise<EditorHost>;
  filePresenter(
    tab: TabOwner,
    content: () => HTMLElement | undefined,
  ): Omit<TabPresenter, "signal">;
  captureTab(tab: TabOwner): void;
  readonly review: {
    interaction: InteractionIntent;
    scope: ReviewScopeState;
    canClose(): boolean;
    overview(): ReviewOverview;
    overviewFor(session: ClientSession): ReviewOverview;
    draftsFor(session: ClientSession): ReviewCommentDrafts;
    diffOptions(
      session: ClientSession,
      diff: ReviewFileDiff,
      reveal: (file: ReviewFile, line: number) => void,
      captureAdvance: (decision: ReviewDecision) => ReviewDecisionCompletion,
    ): InlineDiffOptions;
    toggleFileCollapsed(session: ClientSession, path: string | undefined): boolean;
    setFileCollapsed(session: ClientSession, path: string, collapsed: boolean): void;
    revert(session: ClientSession): boolean;
    keepFile(session: ClientSession, path: string | undefined): boolean;
    revertFile(session: ClientSession, path: string | undefined): boolean;
    close(session: ClientSession): boolean;
    keepAll(session: ClientSession): boolean;
    revertAll(session: ClientSession): boolean;
    undoKeep(session: ClientSession): boolean;
    undoRevert(session: ClientSession): boolean;
    redo(session: ClientSession): boolean;
  };
  readonly tabs: TabActions;
  readonly nav: NavActions;
  /** The omnibar's Go-to-Symbol surface: query document/workspace symbols and live-preview/commit the jump. */
  readonly symbols: () => SymbolActions;
  dispose(): void;
}

export function createEditorController(deps: EditorControllerDeps): EditorController {
  const beginFocus = (session: ClientSession): FocusIntent | undefined =>
    selectedSession() === session ? deps.interaction.begin() : undefined;
  // host + inlineDiff are set once the editor chunk loads and the editor is created (see start).
  let host: EditorHost | undefined;
  let inlineDiff: InlineDiff | undefined;
  const reviewScope: ReviewScopeState = { current: "change" };
  let commentProse: CommentProse | undefined;
  let reviseMarks: ReviseMarks | undefined;
  let initTimer: number | undefined;
  let disposing = false;
  let resolveEditorHost!: (created: EditorHost) => void;
  let rejectEditorHost!: (error: unknown) => void;
  const editorHostReady = new Promise<EditorHost>((resolve, reject) => {
    resolveEditorHost = resolve;
    rejectEditorHost = reject;
  });
  void editorHostReady.catch(() => undefined);
  const editorSessions = new Set<ClientSession>();
  const pendingReconciliations = new Set<ClientSession>();
  const pendingActivations = new WeakMap<ClientSession, Promise<unknown>>();
  // Disposables for the content/model listeners that feed activeContent (the live Preview text).
  let contentSubs: { dispose(): void }[] = [];
  let editorMounted = false;
  const reviews = createReviewStore(captureReviewFor);
  const commentOwners = new Map<ClientSession, ReviewCommentSession>();
  const commentsFor = (session: ClientSession): ReviewCommentSession => {
    if (session.closed) throw new Error("The comment session is closed.");
    let owner = commentOwners.get(session);
    if (!owner) {
      owner = new ReviewCommentSession(
        () => reviews.board(session).files,
        (path, model) =>
          model.uri.scheme === SESSION_FILE_SCHEME &&
          sessionOwnsUri(session, model.uri) &&
          samePath(path, sessionUriHostPath(model.uri)),
        (request) =>
          session
            .feature("review")
            .request<CommandResult, ReviewCommentPost>("addComment", request),
      );
      commentOwners.set(session, owner);
    }
    return owner;
  };
  const reviewProposals = new WeakMap<ClientSession, SessionProposal>();
  const reconcileOpenFiles = (session: ClientSession): void => {
    host?.reconcileSession(
      session,
      openTabsFor(session)
        .filter(isFileTab)
        .map((tab) => tab.path),
    );
  };
  const scheduleReconciliation = (session: ClientSession): void => {
    if (pendingReconciliations.has(session)) {
      return;
    }
    pendingReconciliations.add(session);
    queueMicrotask(() => {
      pendingReconciliations.delete(session);
      if (
        !disposing &&
        editorSessions.has(session) &&
        pendingActivations.get(session) === undefined
      ) {
        reconcileOpenFiles(session);
      }
    });
  };
  const trackActivation = <T>(
    session: ClientSession,
    signal: AbortSignal,
    activation: Promise<T>,
  ): Promise<T> => {
    pendingActivations.set(session, activation);
    const settled = (): void => {
      signal.removeEventListener("abort", settled);
      if (pendingActivations.get(session) === activation) {
        pendingActivations.delete(session);
        scheduleReconciliation(session);
      }
    };
    if (signal.aborted) settled();
    else signal.addEventListener("abort", settled, { once: true });
    void activation.then(settled, settled);
    return activation;
  };
  const rebindSession = async (session: ClientSession): Promise<void> => {
    if (host === undefined || selectedSession() !== session) return;
    clearPresentedProposal();
    host.clear();
    const path = activePathFor(session);
    if (path !== null) {
      const result = activateTabFor(session, path);
      if (result !== null) {
        result.placement = { ...result.placement, focus: false };
        await applyActive(session, result, undefined);
      }
    }
    if (selectedSession() === session) renderReviewState(session);
  };
  // The active file's working-copy text, kept live off the editor model so Preview renders edits/reloads.
  const [activeContent, setActiveContent] = createSignal("");
  // Whether an inline openDiff review currently occupies the editor, so the Preview overlay suspends over it.
  const [reviewActive, setReviewActive] = createSignal(false);
  // The openDiff under inline review (at most one live, since openDiff blocks). `reviewUri` keys the transient
  // review model the inline diff is rendered over.
  let activeReview:
    | {
        session: ClientSession;
        id: string;
        path: string;
        original: string;
        reviewUri: string | undefined;
        // Tab opened purely to show the proposal; on reject, drop it and return to `priorActive`.
        addedTab: boolean;
        // Tab active before the review, restored if an `addedTab` is dropped on reject/cancel.
        priorActive: string | null;
      }
    | undefined;

  const captureLocation = (tab: TabOwner): NavLocation | undefined => {
    const presenter = tab.presentation;
    if (presenter === undefined) return undefined;
    const view = presenter.capture();
    captureViewState(tab, view.state);
    return { tab: { path: tab.entry.path, kind: tabKind(tab.entry) }, view };
  };
  const filePresenter = (
    tab: TabOwner,
    content: () => HTMLElement | undefined,
  ): Omit<TabPresenter, "signal"> => ({
    text: content() === undefined,
    capture: () => {
      const connection = editorContexts.forTab(tab);
      const text = connection?.capture() ?? null;
      return {
        state: text?.viewState ?? tab.viewState,
        text: content() === undefined ? text : null,
      };
    },
    restore: async (placement, signal) => {
      const editorHost = await editorHostReady;
      signal.throwIfAborted();
      if (activeReview?.session === tab.session && samePath(activeReview.path, tab.entry.path))
        return;
      const shown = await editorHost.show(tab.session, tab.entry.path, placement, signal);
      if (shown.kind === "failed") {
        throw new FileOpenError();
      }
      if (shown.kind === "superseded")
        throw new DOMException("Tab activation cancelled", "AbortError");
    },
    focus: () => {
      const element = content();
      if (element === undefined) {
        inlineDiff?.refreshPresentation();
        editorContexts.forTab(tab)?.editor.focus();
      } else focusTabContent(element);
    },
    actions: () => (content() === undefined ? inlineDiff?.captureActions() : undefined),
    captureReviewAdvance: (path, decision) =>
      content() === undefined ? captureFileAdvance(tab.session, path, decision) : () => {},
  });
  const applyActive = (
    session: ClientSession,
    result: ActivateResult,
    focus: FocusIntent | undefined,
  ): Promise<TextEditorConnection | undefined> => {
    if (selectedSession() !== session) return Promise.resolve(undefined);
    const tab = tabOwnerFor(session, result.path);
    if (tab === undefined) return Promise.resolve(undefined);
    deps.onCurrentFileChanged(isFileTab(tab.entry) ? tab.entry.path : null);
    const signal = navigation.signal(session);
    return trackActivation(
      session,
      signal,
      (async () => {
        const presenter = await tab.wait(signal);
        const validity = AbortSignal.any([signal, presenter.signal]);
        validity.throwIfAborted();
        try {
          await presenter.restore(result.placement, validity);
        } catch (error) {
          if (error instanceof FileOpenError && !validity.aborted)
            rollbackFailedOpen(session, result.path, focus);
          throw error;
        }
        validity.throwIfAborted();
        const location = captureLocation(tab);
        if (location !== undefined) navigation.record(session, location);
        if (
          focus?.current() &&
          (!("focus" in result.placement) || result.placement.focus !== false)
        )
          presenter.focus();
        return editorContexts.forTab(tab);
      })(),
    );
  };

  const presentTab = (
    session: ClientSession,
    result: ActivateResult,
    focus: FocusIntent | undefined,
  ): void => {
    void applyActive(session, result, focus).catch((error: unknown) => {
      if (!(error instanceof DOMException && error.name === "AbortError"))
        deps.onOpenError(`Couldn't open the tab: ${String(error)}`);
    });
  };

  // Drop a tab whose open failed (no working copy to release) and, if it was active, switch to its neighbor. A
  // cascade is fine: an unreadable neighbor rolls back in turn until a readable tab or empty pane is reached.
  const rollbackFailedOpen = (
    session: ClientSession,
    path: string,
    focus: FocusIntent | undefined,
  ): void => {
    const wasActive = activePathFor(session);
    const result = closeTabFor(session, path);
    if (result === null) {
      return;
    }
    if (path === wasActive) {
      applyOrClear(session, result.next, focus);
    }
  };

  const focusEditorSurface = (): void => {
    const session = selectedSession();
    const tab = session === null ? undefined : activeTabFor(session);
    if (tab !== undefined) tab.presentation?.focus();
  };

  const activateDestinationFor = (session: ClientSession, intent: EditorOpenIntent): boolean => {
    if (intent !== "restore") navigation.depart(session);
    if (selectedSession() !== session) return false;
    deps.onDestinationActivated();
    return true;
  };

  const openFileFor = (
    session: ClientSession,
    path: string,
    line: number | undefined,
    preview: boolean,
    scratch: boolean,
    intent: EditorOpenIntent,
    focus: FocusIntent | undefined,
  ): void => {
    const foreground = activateDestinationFor(session, intent);
    const result = openTabFor(session, path, {
      ...(line === undefined ? {} : { line }),
      preview,
      scratch,
    });
    if (foreground) {
      presentTab(session, result, focus);
    }
  };

  const openFile = (
    path: string,
    line: number | undefined,
    preview = false,
    scratch = false,
  ): void => {
    const session = selectedSession();
    if (session !== null) {
      openFileFor(session, path, line, preview, scratch, "navigation", deps.interaction.begin());
    }
  };

  const commitFileOpens = (
    session: ClientSession,
    files: ResolvedFileOpen[],
    activePath: string | null,
    focus: FocusIntent | undefined,
  ): Promise<void> => {
    const foreground = activePath !== null && activateDestinationFor(session, "navigation");
    let active: ActivateResult | undefined;
    for (const file of files) {
      const activate = activePath !== null && samePath(file.path, activePath);
      const result = openTabFor(session, file.path, {
        ...(file.line === null ? {} : { line: file.line }),
        preview: file.preview,
        activate,
      });
      if (activate) active = result;
    }
    const committed = session
      .feature("editor")
      .request<boolean, FileOpenCommit>("commitFileOpens", {
        files: files.map(({ path, preview }) => ({ path, preview })),
        activePath,
        originPageEpoch: PAGE_EPOCH,
      });
    if (foreground && active !== undefined) presentTab(session, active, focus);
    return committed.then(() => {});
  };

  // The document/workspace symbol query surface (monaco glue), captured once the editor chunk loads in start().
  const displayedText = (): TextEditorConnection | undefined => {
    const session = selectedSession();
    return session === null ? undefined : editorContexts.get(session);
  };
  const navigateFrom = async (
    source: TextEditorConnection,
    path: string,
    selection: monaco.IRange | undefined,
    preview: boolean,
    target: "source" | "file",
    origin: TextLocation | undefined,
  ): Promise<TextEditorConnection | undefined> => {
    if (!editorContexts.displayed(source) || selectedSession() !== source.session) return undefined;
    const focus = deps.interaction.begin();
    const { session } = source;
    if (origin !== undefined) source.restore(origin);
    navigation.depart(session);
    if (
      origin !== undefined &&
      samePath(origin.path, path) &&
      (target === "source" || isFileTab(source.tab.entry))
    ) {
      if (selection !== undefined) {
        source.editor.setSelection(selection);
        source.editor.revealRangeInCenterIfOutsideViewport(selection, REVEAL_SCROLL);
      }
      source.editor.focus();
      const location = captureLocation(source.tab);
      if (location !== undefined) navigation.push(session, location);
      return source;
    }
    deps.onDestinationActivated();
    const result = openTabFor(session, path, { preview });
    result.placement = selection === undefined ? { line: 1 } : { selection };
    const destination = await applyActive(session, result, focus);
    return destination;
  };
  const symbols = (): SymbolActions => {
    const connection = displayedText();
    const origin = connection?.capture();
    if (connection === undefined || origin === undefined) return noEditorSymbols;
    return createEditorSymbols({
      connection,
      origin,
      suspendHistory: () => navHistoryFor(connection.session).suspend(),
      commit: (origin, symbol) => {
        void navigateFrom(connection, symbol.path, symbol.range, false, "source", origin);
      },
    });
  };

  const [navRevision, setNavRevision] = createSignal(0);
  const navigation = createEditorNavigation({
    capture: (session) => {
      const tab = activeTabFor(session);
      return tab === undefined ? undefined : captureLocation(tab);
    },
    restore: async (session, location, signal) => {
      signal.throwIfAborted();
      if (selectedSession() !== session)
        throw new DOMException("Editor view detached", "AbortError");
      const focus = deps.interaction.begin();
      activateDestinationFor(session, "restore");
      const result = openTabFor(session, location.tab.path, { kind: tabKind(location.tab) });
      result.placement = { viewState: location.view.state };
      await applyActive(session, result, focus);
      signal.throwIfAborted();
    },
    changed: () => setNavRevision((revision) => revision + 1),
    failed: (_session, error) =>
      deps.onOpenError(`Couldn't restore editor location: ${String(error)}`),
  });
  const navHistoryFor = navigation.history;
  const offEditorLocations = editorContexts.onChange((connection) => {
    const location = captureLocation(connection.tab);
    if (location !== undefined)
      navigation.schedule(connection.session, location, connection.signal);
  });

  // Open an http(s) URL as a web (iframe) tab. No Monaco model / working copy — App renders an iframe over the
  // editor host when this tab is active. Independent of the editor chunk, so it works before Monaco is up.
  const openWebTab = (url: string): void => {
    const session = selectedSession();
    if (session !== null) {
      activateDestinationFor(session, "navigation");
      presentTab(session, openTabFor(session, url, { kind: "web" }), deps.interaction.begin());
    }
  };

  // Open a fetched source doc (Notion) as a source tab, keyed by its target. No Monaco model — App overlays the
  // SourceView shadow-root render over the editor host when this tab is active; SourceView reads the html by target.
  const openSourceTab = (target: string): void => {
    const session = selectedSession();
    if (session !== null) {
      activateDestinationFor(session, "navigation");
      presentTab(
        session,
        openTabFor(session, target, { kind: "source" }),
        deps.interaction.begin(),
      );
    }
  };

  // Switch the editor off a closing tab before its working copy is released, else clear to an empty pane.
  const applyOrClear = (
    session: ClientSession,
    next: ActivateResult | null,
    focus: FocusIntent | undefined,
  ): void => {
    if (selectedSession() !== session) {
      return;
    }
    if (next !== null) {
      presentTab(session, next, focus);
    } else {
      host?.clear();
      deps.onCurrentFileChanged(null);
    }
  };

  const basename = (path: string): string => path.split(/[\\/]/).pop() ?? path;

  const tabs = createTabActions({
    captureFocus: beginFocus,
    depart: (session) => {
      activateDestinationFor(session, "navigation");
    },
    present: applyOrClear,
    capture: (tab) => {
      captureLocation(tab);
    },
    content: (tab) => host?.contentOf(tab.session, tab.entry.path) ?? "",
    release: (tab) => {
      if (!isFileTab(tab.entry)) return;
      host?.closeFile(tab.session, tab.entry.path, tab.entry.scratch === true);
      if (tab.entry.scratch)
        tab.session.feature("editor").publish("discardScratch", { path: tab.entry.path });
    },
    confirmDiscard: deps.confirmDiscard,
  });

  const resolveReview = (keep: boolean): void => {
    const review = activeReview;
    if (review === undefined) {
      return;
    }
    activeReview = undefined;
    setReviewActive(false);
    // endReview returns the proposal's final content (which Claude writes to disk on keep) and restores the
    // editor off the transient review model. The review never dirtied the working copy.
    const finalContents = host?.endReview(review.session, review.path, keep, review.original) ?? "";
    if (review.reviewUri !== undefined) {
      inlineDiff?.clearByUri(review.reviewUri);
    }
    // A rejected proposal whose tab was opened just to review it: drop it and fall back to the previously
    // active tab (a store-only fixup; endReview already restored the editor). A kept file stays open.
    if (!keep && review.addedTab) {
      dropReviewTabFor(review.session, review.path, review.priorActive);
    }
    reviewProposals.delete(review.session);
    if (selectedSession() === review.session) {
      deps.onCurrentFileChanged(activePathFor(review.session));
    }
    review.session.feature("editor").publish("resolveDiff", {
      id: review.id,
      kept: keep,
      finalContents: keep ? finalContents : "",
    });
  };

  // Brings up the editor, holding the splash until a deterministic outcome — editor ready or a real failure
  // (chunk load, crash, or an init that never settles within EDITOR_INIT_MS) — so the reveal shows a settled UI.
  const start = (container: HTMLElement): void => {
    const editorReady = import("./editor-host").then(({ createEditorHost }) =>
      createEditorHost(
        container,
        deps.onSaveError,
        deps.onOpenError,
        ({ path, selection, source }) =>
          navigateFrom(source, path, selection, true, "file", source.capture()),
      ),
    );
    const initDeadline = new Promise<never>((_, reject) => {
      initTimer = window.setTimeout(
        () => reject(new Error(`editor init did not settle within ${EDITOR_INIT_MS}ms`)),
        EDITOR_INIT_MS,
      );
    });
    void Promise.race([editorReady, initDeadline])
      .then(async (created) => {
        host = created;
        for (const session of editorSessions) {
          reconcileOpenFiles(session);
        }
        // inline-diff + comment-prose pull Monaco; import them here (the chunk is already loaded by the
        // editor host above) so they stay off the first-paint entry chunk.
        const [diff, prose, marks] = await Promise.all([
          import("./inline-diff"),
          import("./comment-prose"),
          import("./revise-marks"),
        ]);
        inlineDiff = diff.createInlineDiff(created.editor, {
          scope: reviewScope,
          active: () => {
            const connection = editorContexts.fromEditor(created.editor);
            return connection !== undefined && editorContexts.displayed(connection);
          },
          toolbarHost: () =>
            editorContexts.fromEditor(created.editor) === undefined
              ? null
              : created.editor.getDomNode(),
          revealLine: (line) => created.editor.revealLineInCenter(line, REVEAL_SCROLL),
          reviewLine: () => diff.inlineReviewLine(created.editor),
          prepareGeometry: () => {},
          painted: () => {},
          updateGeometry: (change) => change(),
        });
        // Track the active model's text so the Preview overlay renders live (edits, Claude writes, reloads).
        const syncContent = (): void => {
          setActiveContent(created.editor.getModel()?.getValue() ?? "");
        };
        contentSubs = [
          created.editor.onDidChangeModelContent(() => syncContent()),
          created.editor.onDidChangeModel(() => {
            syncContent();
          }),
        ];
        syncContent();
        // Suspended over a model with a live inline diff so a collapsed comment never hides a changed line.
        commentProse = prose.createCommentProse(created.editor, {
          isBlocked: (uri) => inlineDiff?.hasDiffForUri(uri) ?? false,
        });
        reviseMarks = marks.sharedReviseMarks;
        resolveEditorHost(created);
        container.setAttribute("data-ready", "true");
        editorMounted = true;
        const session = selectedSession();
        if (session !== null && !pendingActivations.has(session)) {
          await rebindSession(session);
        } else if (session !== null) {
          renderReviewState(session);
        }
        // Reflect whatever file the editor ended up showing (replayed pending-open or hot-reload restore).
        const model = created.editor.getModel();
        if (model !== null && model.uri.scheme === SESSION_FILE_SCHEME) {
          deps.onCurrentFileChanged(sessionUriHostPath(model.uri));
        }
        mark("editor-ready");
      })
      .catch((error: unknown) => {
        rejectEditorHost(error);
        log("error", `editor init failed: ${String(error)}`);
        // The pane is now dead (host stays undefined, every openFile silently queues), so tell the user
        // rather than leave a blank editor that swallows clicks.
        deps.onOpenError("The editor failed to load. Reload the window to try again.");
      })
      .finally(() => {
        window.clearTimeout(initTimer);
        dismissSplash();
      });
  };

  // Open a review file on its first change as a preview tab (so ← / → reuses one tab); re-requests its turn-diff
  // so applied markers render even if the push was missed.
  const openReviewFile = (
    session: ClientSession,
    file: ReviewFile,
    line: number,
    focus: FocusIntent | undefined,
  ): void => {
    if (selectedSession() !== session) {
      return;
    }
    if (!file.currentExists) {
      showUnifiedReview(session, focus);
      return;
    }
    openFileFor(session, file.path, line, true, false, "navigation", focus);
    session.feature("review").publish("showFile", { path: file.path });
  };

  const showUnifiedReview = (session: ClientSession, focus: FocusIntent | undefined): boolean => {
    if (!activateDestinationFor(session, "navigation")) return false;
    const result = openTabFor(session, REVIEW_TAB_KEY, { kind: "review" });
    presentTab(session, result, focus);
    return true;
  };

  // Monotonic revision of the published review set.
  let reviewRev = 0;
  let presentedReviewSession: ClientSession | null = null;
  // Reflect the review set onto the inline-diff's parked navigator: it surfaces (parked at "change 0", editor
  // untouched) whenever files are pending and none is in view, so review is visible the moment changes land —
  // stepping in (a nav key) opens the first change. Called wherever the review board changes.
  const updateParkedReview = (session: ClientSession | null): void => {
    const state = session === null ? null : reviews.board(session);
    const files = state?.files.map((file) => file.summary()) ?? [];
    const label = state?.label ?? "";
    // Publish the live review-walk set for e2e / diagnostics (read-only) — a failed PR-switch test attaches
    // exactly which files the navigator holds, so a leaked cross-PR mix is visible without walking it. `rev`
    // is a monotonic counter bumped on every change so a test can detect quiescence exactly (poll-sampling
    // the file list alone can miss a fast bounce during a rapid switch storm's push drain).
    reviewRev += 1;
    window.__WEAVIE_REVIEW__ = {
      files: files.map((file) => file.path),
      label,
      rev: reviewRev,
    };
    const stepIn = (): void => {
      const first = files[0];
      if (session !== null && first !== undefined) {
        revealReviewFile(session, first, first.line);
      }
    };
    inlineDiff?.setParkedReview(
      files.length > 0
        ? {
            fileCount: files.length,
            ...(label !== "" ? { label } : {}),
            stepIn,
            nextFile: stepIn,
            prevFile: stepIn,
          }
        : undefined,
    );
  };

  const resetPresentedReview = (nextOwner: ClientSession | null): void => {
    presentedReviewSession = nextOwner;
    inlineDiff?.clearAll();
    inlineDiff?.setReviewHistory({
      canUndo: false,
      canUndoKeep: false,
      canUndoRevert: false,
      canRedo: false,
    });
    commentProse?.refresh();
  };

  const ownPresentedReview = (session: ClientSession): void => {
    if (presentedReviewSession !== session) {
      resetPresentedReview(session);
    }
  };

  const revealReviewFile = (session: ClientSession, file: ReviewFile, line: number): void =>
    openReviewFile(session, file, line, beginFocus(session));

  const captureFileAdvance = (
    session: ClientSession,
    path: string,
    decision: ReviewDecision,
  ): ReviewDecisionCompletion => {
    if (decision === "keepFile") return () => {};
    const tab = activeTabFor(session);
    if (selectedSession() !== session || tab === undefined || !samePath(tab.entry.path, path))
      return () => {};
    const presenter = tab.presentation;
    const signal = navigation.signal(session);
    return (location, focus, { sourceDeleted, sourceHasReview }) => {
      if (decision === "revertFile" && sourceHasReview) return;
      if (signal.aborted || !focus.current() || selectedSession() !== session) return;
      if (tab.signal.aborted && !sourceDeleted) return;
      if (!tab.signal.aborted && (activeTabFor(session) !== tab || tab.presentation !== presenter))
        return;
      const next = reviews
        .board(session)
        .files.find((file) => samePath(file.summary().path, location.path))
        ?.summary();
      if (next !== undefined) openReviewFile(session, next, location.line, focus);
    };
  };

  const captureReviewAdvance = (
    session: ClientSession,
    path: string,
    decision: ReviewDecision,
  ): ReviewDecisionCompletion =>
    selectedSession() === session
      ? (activeTabFor(session)?.presentation?.captureReviewAdvance(path, decision) ?? (() => {}))
      : () => {};

  // Flush the file's pending save (so the host reverts from current disk content), then run `send`. Both the
  // per-hunk and whole-file reverts go through this so the host never races a debounced write. A failed flush
  // means the revert would act against stale disk content, so surface it and abort rather than misapply silently.
  const afterFlush = (session: ClientSession, path: string, send: () => void): void => {
    const flushed = host?.flush(session, path);
    if (flushed === undefined) {
      send();
      return;
    }
    flushed.then(send, (error: unknown) => {
      deps.onSaveError(
        `Couldn't save ${basename(path)} before reverting — revert aborted: ${String(error)}`,
      );
    });
  };

  const decide = <T extends { path: string }>(
    session: ClientSession,
    operation: ReviewDecision,
    payload: T,
    complete: ReviewDecisionCompletion,
  ): void => {
    const focus = beginFocus(session);
    afterFlush(session, payload.path, () => {
      void applyReviewDecision(session, operation, payload, focus, complete);
    });
  };

  // Un-keep just this faded hunk: the host splices the accepted-anchor lines back into the review baseline, so it
  // returns to the bright pending band. No disk read (the guard is against Core's review baseline), so no flush.
  const unkeepHunk = (session: ClientSession, path: string, hunk: HunkUnkeep): void => {
    session.feature("review").publish("unkeepHunk", { path, ...hunk });
  };

  const pendingReviewFile = (
    session: ClientSession,
    path: string | undefined,
  ): ReviewFile | null => {
    const target = path;
    const file = reviews
      .board(session)
      .files.find(
        (candidate) => target !== undefined && samePath(candidate.summary().path, target),
      );
    const diff = file?.diff();
    return file === undefined ||
      diff === null ||
      diff === undefined ||
      (diff.baseline === diff.current && diff.baselineExists === diff.currentExists)
      ? null
      : file.summary();
  };

  // Revert every change in one file to its turn baseline on disk, after a confirm (the host restores the file
  // wholesale and re-emits its now-empty diff + the trimmed review set).
  const revertFile = (
    session: ClientSession,
    path: string,
    complete: ReviewDecisionCompletion,
  ): void => {
    void deps
      .confirm({
        title: "Revert file?",
        body: `Discard all changes to "${basename(path)}" and restore it to before this turn? You can undo this afterward.`,
        confirmLabel: "Revert file",
      })
      .then((ok) => {
        if (ok) {
          decide(session, "revertFile", { path }, complete);
        }
      });
  };

  // Revert the whole turn (revert all), after a confirm — the host reverts every touched file to its baseline.
  const revertAllFor = (session: ClientSession): void => {
    const count = reviews.board(session).files.length;
    void deps
      .confirm({
        title: "Revert all changes?",
        body: `Discard every change from this turn${count > 1 ? ` across ${count} files` : ""}? You can undo this afterward.`,
        confirmLabel: "Revert all",
      })
      .then((ok) => {
        if (ok) {
          session.feature("review").publish("revertAll", {});
        }
      });
  };

  const tryRevertAll = (session: ClientSession): boolean => {
    if (reviews.board(session).files.length === 0) {
      return false;
    }
    revertAllFor(session);
    return true;
  };

  const fileHistoryOwners = new WeakMap<ClientSession, ReviewHistoryHandlers>();
  const fileHistoryHandlers = (session: ClientSession): ReviewHistoryHandlers => {
    const existing = fileHistoryOwners.get(session);
    if (existing !== undefined) return existing;
    const handlers = reviewHistoryHandlers(
      session,
      () => beginFocus(session),
      () => {
        const connection = host === undefined ? undefined : editorContexts.fromEditor(host.editor);
        const presentation = connection?.tab.presentation;
        return ({ path, line }, focus) => {
          if (
            connection === undefined ||
            presentation?.signal.aborted ||
            !editorContexts.displayed(connection) ||
            selectedSession() !== session
          )
            return;
          const file = reviews
            .board(session)
            .files.find((file) => samePath(file.summary().path, path));
          if (file !== undefined) openReviewFile(session, file.summary(), line, focus);
        };
      },
    );
    fileHistoryOwners.set(session, handlers);
    return handlers;
  };

  const undoReview = (session: ClientSession, kind: "keep" | "revert"): boolean => {
    const history = reviews.board(session).history;
    if (kind === "keep" ? !history.canUndoKeep : !history.canUndoRevert) {
      return false;
    }
    const handlers = reviewHistoryHandlers(
      session,
      () => beginFocus(session),
      () => () => {},
    );
    if (kind === "keep") handlers.onUndoKeep();
    else handlers.onUndoRevert();
    return true;
  };

  const redoReview = (session: ClientSession): boolean => {
    if (!reviews.board(session).history.canRedo) {
      return false;
    }
    reviewHistoryHandlers(
      session,
      () => beginFocus(session),
      () => () => {},
    ).onRedo();
    return true;
  };

  const clearPresentedProposal = (): void => {
    const review = activeReview;
    if (review === undefined) {
      return;
    }
    activeReview = undefined;
    setReviewActive(false);
    host?.endReview(review.session, review.path, false, review.original);
    if (review.reviewUri !== undefined) {
      inlineDiff?.clearByUri(review.reviewUri);
    }
  };

  const presentProposal = (session: ClientSession, proposal: SessionProposal): void => {
    const editorHost = host;
    if (editorHost === undefined || selectedSession() !== session) {
      return;
    }
    if (activeReview?.session === session && activeReview.id === proposal.id) {
      setReviewActive(true);
      if (activeReview.reviewUri !== undefined) {
        inlineDiff?.setByUri(activeReview.reviewUri, {
          original: proposal.original,
          claudeVersion: proposal.proposed,
          mode: "review",
          onAccept: () => resolveReview(true),
          onReject: () => resolveReview(false),
        });
      }
      return;
    }

    clearPresentedProposal();
    const reviewUri = editorHost.beginReview(session, proposal.path, proposal.proposed, 1);
    activeReview = { session, reviewUri, ...proposal };
    setReviewActive(true);
    inlineDiff?.setByUri(reviewUri, {
      original: proposal.original,
      claudeVersion: proposal.proposed,
      mode: "review",
      onAccept: () => resolveReview(true),
      onReject: () => resolveReview(false),
    });
  };

  const appliedReviewOptions = (
    session: ClientSession,
    message: ReviewFileDiff,
    reveal: (file: ReviewFile, line: number) => void,
    captureAdvance: (decision: ReviewDecision) => ReviewDecisionCompletion,
  ): InlineDiffOptions => {
    const state = reviews.board(session);
    const files = state.files.map((file) => file.summary());
    const index = files.findIndex((file) => samePath(file.path, message.path));
    const commenting = commentsFor(session).context(message.path);
    const fileNavigation =
      files.length > 1 && index !== -1
        ? {
            onPrevFile: (): void => {
              const file = files[(index - 1 + files.length) % files.length]!;
              reveal(file, file.line);
            },
            onNextFile: (): void => {
              const file = files[(index + 1) % files.length]!;
              reveal(file, file.line);
            },
            fileIndex: index + 1,
            fileCount: files.length,
          }
        : {};
    return {
      original: message.baseline,
      acceptedBaseline: message.acceptedBaseline,
      claudeVersion: message.current,
      mode: "applied",
      onKeepHunk: (hunk) =>
        decide(session, "keepHunk", { path: message.path, ...hunk }, captureAdvance("keepHunk")),
      onKeepFile: () =>
        decide(session, "keepFile", { path: message.path }, captureAdvance("keepFile")),
      onRevertHunk: (hunk) =>
        decide(
          session,
          "revertHunk",
          { path: message.path, ...hunk },
          captureAdvance("revertHunk"),
        ),
      onRevertFile: () => revertFile(session, message.path, captureAdvance("revertFile")),
      onUnkeepHunk: (hunk) => unkeepHunk(session, message.path, hunk),
      onKeepAll: () => session.feature("review").publish("accept", {}),
      onUndo: () => revertAllFor(session),
      fileLabel: message.name,
      ...(state.label !== "" ? { reviewLabel: state.label } : {}),
      ...fileNavigation,
      ...(commenting === undefined ? {} : { commenting }),
    };
  };

  const renderTurnDiff = (session: ClientSession, message: ReviewFileDiff): void => {
    if (
      message.acceptedBaseline === message.current &&
      message.acceptedBaselineExists === message.currentExists
    ) {
      inlineDiff?.clear(session, message.path);
      commentProse?.refresh();
      return;
    }

    inlineDiff?.set(
      session,
      message.path,
      appliedReviewOptions(
        session,
        message,
        (file, line) => revealReviewFile(session, file, line),
        (decision) => captureFileAdvance(session, message.path, decision),
      ),
    );
    commentProse?.refresh();
  };

  const renderReviewState = (session: ClientSession): void => {
    if (selectedSession() !== session) {
      return;
    }
    ownPresentedReview(session);
    const state = reviews.board(session);
    const proposal = reviewProposals.get(session) ?? null;
    if (
      activeReview !== undefined &&
      (activeReview.session !== session || proposal === null || activeReview.id !== proposal.id)
    ) {
      clearPresentedProposal();
    }
    updateParkedReview(session);
    inlineDiff?.bindHistory(fileHistoryHandlers(session));
    inlineDiff?.setReviewHistory(state.history);
    const retained: string[] = [];
    for (const file of state.files) {
      const diff = file.diff();
      if (diff !== null) {
        retained.push(diff.path);
        renderTurnDiff(session, diff);
      }
    }
    if (proposal !== null) {
      presentProposal(session, proposal);
    }
    inlineDiff?.retainApplied(session, retained);
    commentProse?.refresh();
  };

  const setReviewFilesFor = (session: ClientSession, files: ReviewFile[], label: string): void => {
    if (
      canCloseReview(reviews.board(session)) &&
      !canCloseReview({ files, label }) &&
      !commentOwners.get(session)?.drafts.retained().length
    )
      void tabs.capture(session, REVIEW_TAB_KEY).close();
    reviews.setFiles(session, files, label);
    commentOwners.get(session)?.refresh();
    renderReviewState(session);
  };

  const setTurnDiffFor = (session: ClientSession, message: ReviewFileDiff): void => {
    reviews.setDiff(session, message);
    commentOwners.get(session)?.refresh();
    if (selectedSession() === session) {
      renderTurnDiff(session, message);
    }
  };

  const setReviewCommentsFor = (session: ClientSession, message: ReviewComments): void => {
    const state = reviews.setComments(session, message);
    commentOwners.get(session)?.refresh();
    if (selectedSession() !== session) {
      return;
    }
    const diff = state.files.find((file) => samePath(file.summary().path, message.path))?.diff();
    if (diff !== null && diff !== undefined) {
      renderTurnDiff(session, diff);
    }
  };

  const resetReviewFor = (session: ClientSession): void => {
    if (selectedSession() === session) {
      resetPresentedReview(session);
    }
    reviews.reset(session);
    commentOwners.get(session)?.refresh();
    reviewProposals.delete(session);
    renderReviewState(session);
  };

  const showProposal = (session: ClientSession, message: DiffProposal): void => {
    const priorActive = activePathFor(session);
    const addedTab = !openTabsFor(session).some((tab) => samePath(tab.path, message.path));
    reviewProposals.set(session, { ...message, priorActive, addedTab });
    activateDestinationFor(session, "navigation");
    openTabFor(session, message.path, {});
    renderReviewState(session);
  };

  const closeProposal = (session: ClientSession, id: string): void => {
    const proposal = reviewProposals.get(session) ?? null;
    if (proposal === null || proposal.id !== id) {
      return;
    }
    reviewProposals.delete(session);
    if (activeReview?.session === session && activeReview.id === id) {
      clearPresentedProposal();
    }
    if (proposal.addedTab) {
      dropReviewTabFor(session, proposal.path, proposal.priorActive);
    }
    if (selectedSession() === session) {
      deps.onCurrentFileChanged(activePathFor(session));
      renderReviewState(session);
    }
  };

  const replaceProposals = (session: ClientSession, proposals: DiffProposal[]): void => {
    const next = proposals.at(-1);
    const current = reviewProposals.get(session) ?? null;
    if (next === undefined) {
      if (current !== null) {
        closeProposal(session, current.id);
      }
      return;
    }
    if (current?.id === next.id) {
      reviewProposals.set(session, { ...current, ...next });
      renderReviewState(session);
      return;
    }
    if (current !== null) {
      closeProposal(session, current.id);
    }
    showProposal(session, next);
  };

  const handleFileChanges = (
    session: ClientSession,
    changes: { path: string; kind: "updated" | "added" | "deleted" }[],
  ): void => {
    for (const change of changes) {
      if (change.kind !== "deleted") {
        continue;
      }
      const entry = openTabsFor(session).find((tab) => samePath(tab.path, change.path));
      if (entry === undefined) {
        continue;
      }
      const wasActive = activePathFor(session) === entry.path;
      const result = closeTabFor(session, entry.path);
      if (result !== null && wasActive) {
        applyOrClear(session, result.next, undefined);
      }
      host?.closeFile(session, entry.path, true);
    }
    renderReviewState(session);
  };

  const offSessionFeatures = registerSessionFeature((session) => {
    const offContext = editorContexts.own(
      session,
      () => activeTabFor(session),
      deps.onEditorContextMenu,
    );
    editorSessions.add(session);
    const editor = session.feature("editor");
    const review = session.feature("review");
    const files = session.feature("files");
    const revise = session.feature("revise");
    const fileNavigation = createFileNavigation(session, {
      beginFocus: () => beginFocus(session),
      navigationSignal: () => navigation.signal(session),
      commit: (files, activePath, focus) => commitFileOpens(session, files, activePath, focus),
      ambiguous: (query, line) => focusOmnibarFileSearch(query, line ?? undefined),
      error: deps.onOpenError,
    });
    const cleanups = [
      ownFileNavigation(session, fileNavigation),
      fileNavigation.dispose,
      editor.handle<Record<string, never>, { session: EditorSession }>("flush", async () => {
        await host?.flushSession(session);
        for (const entry of openTabsFor(session))
          captureLocation(tabOwnerFor(session, entry.path)!);
        flushEditorSessionFor(session);
        return { session: snapshotEditorSessionFor(session) ?? { active: null, open: [] } };
      }),
      editor.on<{
        path: string;
        line: number | null;
        preview?: boolean;
        scratch?: boolean;
        intent: EditorOpenIntent;
      }>("openFile", (message) => {
        openFileFor(
          session,
          message.path,
          message.line ?? undefined,
          message.preview === true,
          message.scratch === true,
          message.intent,
          beginFocus(session),
        );
      }),
      editor.on<{ id: string; path: string; title: string; markdown: string }>(
        "agentPlan",
        (message) => {
          setAgentPlan(session, message.path, message.id, message.title, message.markdown);
        },
      ),
      editor.on<{ path: string }>("agentPlanRemoved", (message) => {
        removeAgentPlan(session, message.path);
      }),
      editor.on<{ path: string; kind: "web" | "source" | "plan" }>(
        "openOverlay",
        ({ path, kind }) => {
          const result = openTabFor(session, path, { kind });
          if (activateDestinationFor(session, "navigation")) {
            presentTab(session, result, deps.interaction.begin());
          }
        },
      ),
      editor.on<DiffProposal>("showDiff", (message) => showProposal(session, message)),
      editor.on<{ proposals: DiffProposal[] }>("diffSnapshot", ({ proposals }) =>
        replaceProposals(session, proposals),
      ),
      editor.on<{ id: string }>("closeDiff", ({ id }) => closeProposal(session, id)),
      editor.on<{ path: string }>("closeTab", ({ path }) => tabs.capture(session, path).close()),
      review.on<{ label: string; files: ReviewFile[] }>("changes", ({ label, files }) =>
        setReviewFilesFor(session, files, label),
      ),
      review.on<ReviewFileDiff>("diff", (message) => setTurnDiffFor(session, message)),
      review.on<ReviewComments>("comments", (message) => setReviewCommentsFor(session, message)),
      review.on("reset", () => resetReviewFor(session)),
      revise.on<{ regions: ReviseRegion[] }>("state", ({ regions }) =>
        reviseMarks?.set(session, regions),
      ),
      // The host asks before it writes: only this page knows whether the buffer is dirty or the region moved.
      revise.handle<{ id: number }, { ok: boolean; reason: string }>("confirm", ({ id }) => {
        const refusal = reviseMarks?.verify(session, id) ?? null;
        return { ok: refusal === null, reason: refusal ?? "" };
      }),
      review.on<ReviewHistory>("history", (history) => {
        const state = reviews.setHistory(session, history);
        if (selectedSession() === session) {
          inlineDiff?.bindHistory(fileHistoryHandlers(session));
          inlineDiff?.setReviewHistory(state.history);
        }
      }),
      files.on<{
        changes: { path: string; kind: "updated" | "added" | "deleted" }[];
      }>("changed", ({ changes }) => handleFileChanges(session, changes)),
      onEditorSessionChanged(session, () => scheduleReconciliation(session)),
      session.state.editor.subscribe((restored) => {
        if (restored?.review != null) {
          reviews.restore(session, restored.review);
        }
        if (restored !== null && editorMounted && selectedSession() === session) {
          void rebindSession(session).catch((error: unknown) => {
            log("error", `editor session restore failed: ${String(error)}`);
            deps.onOpenError(`Couldn't restore the editor session: ${String(error)}`);
          });
        }
      }),
    ];
    return () => {
      for (const cleanup of cleanups) {
        cleanup();
      }
      offContext();
      navigation.detach(session);
      editorSessions.delete(session);
      fileHistoryOwners.delete(session);
      commentOwners.get(session)?.dispose();
      commentOwners.delete(session);
      pendingReconciliations.delete(session);
      if (!disposing) {
        host?.reconcileSession(session, []);
      }
    };
  });

  let presentedSession: ClientSession | null = selectedSession();
  const offSelection = onSelectedSession((session) => {
    if (presentedSession !== null && presentedSession !== session) {
      navigation.capture(presentedSession);
      flushEditorSessionFor(presentedSession);
      navigation.detach(presentedSession);
    }
    presentedSession = session;
    if (!editorMounted) {
      reviews.select(session);
      return;
    }
    reviews.select(session);
    if (session === null) {
      clearPresentedProposal();
      resetPresentedReview(null);
      updateParkedReview(null);
      host?.clear();
      deps.onCurrentFileChanged(null);
      return;
    }
    ownPresentedReview(session);
    void rebindSession(session).catch((error: unknown) => {
      log("error", `editor session rebind failed: ${String(error)}`);
      deps.onOpenError(`Couldn't switch editor sessions: ${String(error)}`);
    });
  });

  interface ScratchSaveResult {
    scratchPath: string;
    savedPath: string;
  }

  const applyScratchSave = (
    session: ClientSession,
    result: ScratchSaveResult,
    focus: FocusIntent | undefined,
  ): void => {
    if (result.savedPath === "") {
      return;
    }
    const activation = convertScratchFor(session, result.scratchPath, result.savedPath);
    if (activation !== null) {
      presentTab(session, activation, focus);
    }
    host?.closeFile(session, result.scratchPath, true);
  };

  // Ask the host to create a scratch buffer; it comes back as an open-file with `scratch: true`.
  const newFile = (session: ClientSession): void => {
    session.feature("editor").publish("newScratch", {});
  };

  const saveScratch = async (tab: TabOwner): Promise<void> => {
    const { session, entry } = tab;
    const native = !isBrowserHostedShell() && session.connection.isLocal;
    const name = native ? basename(entry.path) : await deps.promptScratchName(basename(entry.path));
    if (name === null) return;
    const focus = beginFocus(session);
    tab.assertLive();
    host?.cancelSave(session, entry.path);
    const payload = {
      path: entry.path,
      content: host?.contentOf(session, entry.path) ?? "",
      ...(native ? { suggestedName: name } : { name }),
    };
    const result = await session
      .feature("editor")
      .request<ScratchSaveResult, typeof payload>(
        native ? "saveScratchAs" : "saveScratchNamed",
        payload,
      );
    if (!tab.signal.aborted) applyScratchSave(session, result, focus);
  };

  const save = (tab: TabOwner): boolean => {
    tab.assertLive();
    if (tab.entry.scratch)
      void saveScratch(tab).catch((error: unknown) => tab.session.connection.reportError(error));
    return true;
  };

  return {
    start,
    openFile,
    openWebTab,
    openSourceTab,
    focusEditor: focusEditorSurface,
    reviseSelection: ({ session, model }, selection) => {
      if (
        session === null ||
        model == null ||
        selection == null ||
        model.uri.scheme !== SESSION_FILE_SCHEME
      ) {
        return;
      }
      const startLine = selection.startLineNumber;
      // A selection ending at column 1 stops before that line, so the line isn't part of the region.
      const endLine =
        selection.endColumn === 1 && selection.endLineNumber > startLine
          ? selection.endLineNumber - 1
          : selection.endLineNumber;
      // Line content joined with \n: the host splices line ranges and compares its guard the same way, so a
      // CRLF file must not send the model's \r\n back.
      const originalText = model
        .getLinesContent()
        .slice(startLine - 1, endLine)
        .join("\n");
      const path = sessionUriHostPath(model.uri);
      void deps.promptRevision(endLine - startLine + 1).then((instruction) => {
        if (instruction === null) {
          return;
        }
        // Flush the pending save first, so the host's guard reads the same content the editor shows.
        afterFlush(session, path, () =>
          session.feature("revise").publish("start", {
            path,
            startLine,
            endLineExclusive: endLine + 1,
            originalText,
            instruction,
          }),
        );
      });
    },
    openMatch: (path, line, column, focus) => {
      const session = selectedSession();
      if (session !== null) {
        if (focus) {
          activateDestinationFor(session, "navigation");
        } else {
          navigation.depart(session);
        }
        presentTab(
          session,
          openTabFor(session, path, { line, column, focus, preview: true }),
          deps.interaction.begin(),
        );
      }
    },
    newFile,
    save,
    flushDirty: () => host?.flushDirty() ?? Promise.resolve(),
    flushSession: (session) => host?.flushSession(session) ?? Promise.resolve(),
    openReview: (session, path, line) => {
      if (selectedSession() !== session) {
        return false;
      }
      if (path === undefined) {
        return showUnifiedReview(session, deps.interaction.begin());
      }
      const view = reviews
        .board(session)
        .files.find((candidate) => samePath(candidate.summary().path, path));
      if (view === undefined) {
        return false;
      }
      const file = view.summary();
      revealReviewFile(session, file, line ?? file.line);
      return true;
    },
    hostReady: editorHostReady,
    filePresenter,
    captureTab: (tab) => {
      captureLocation(tab);
    },
    activeContent,
    reviewActive,
    parkedReviewCount: reviews.count,
    review: {
      interaction: deps.interaction,
      scope: reviewScope,
      canClose: () => canCloseReview(reviews.overview()),
      overview: reviews.overview,
      overviewFor: reviews.overviewFor,
      draftsFor: (session) => commentsFor(session).drafts,
      diffOptions: appliedReviewOptions,
      toggleFileCollapsed: (session, path) => {
        const state = reviews.board(session);
        const target = state.files.find(
          (candidate) => path !== undefined && samePath(candidate.summary().path, path),
        );
        if (target === undefined) {
          return false;
        }
        reviews.setFileCollapsed(session, target.summary().path, !target.collapsed());
        return true;
      },
      setFileCollapsed: (session, path, collapsed) => {
        reviews.setFileCollapsed(session, path, collapsed);
      },
      revert: tryRevertAll,
      keepFile: (session, path) => {
        const file = pendingReviewFile(session, path);
        if (file === null) {
          return false;
        }
        decide(
          session,
          "keepFile",
          { path: file.path },
          captureReviewAdvance(session, file.path, "keepFile"),
        );
        return true;
      },
      revertFile: (session, path) => {
        const file = pendingReviewFile(session, path);
        if (file === null) {
          return false;
        }
        revertFile(session, file.path, captureReviewAdvance(session, file.path, "revertFile"));
        return true;
      },
      close: (session) => {
        if (!canCloseReview(reviews.board(session))) return false;
        session.feature("review").publish("close", {});
        return true;
      },
      keepAll: (session) => {
        if (reviews.board(session).files.length === 0) {
          return false;
        }
        session.feature("review").publish("accept", {});
        return true;
      },
      revertAll: (session) => {
        return tryRevertAll(session);
      },
      undoKeep: (session) => undoReview(session, "keep"),
      undoRevert: (session) => undoReview(session, "revert"),
      redo: redoReview,
    },
    tabs,
    nav: {
      back: (session) => {
        if (selectedSession() !== session) return false;
        navigation.capture(session);
        const acted = session !== null && navHistoryFor(session).back();
        setNavRevision((revision) => revision + 1);
        return acted;
      },
      forward: (session) => {
        if (selectedSession() !== session) return false;
        const acted = session !== null && navHistoryFor(session).forward();
        setNavRevision((revision) => revision + 1);
        return acted;
      },
      canBack: () => {
        navRevision();
        const session = selectedSession();
        return session !== null && navHistoryFor(session).canBack();
      },
      canForward: () => {
        navRevision();
        const session = selectedSession();
        return session !== null && navHistoryFor(session).canForward();
      },
    },
    symbols,
    dispose: () => {
      disposing = true;
      window.clearTimeout(initTimer);
      navigation.dispose();
      offEditorLocations();
      for (const sub of contentSubs) {
        sub.dispose();
      }
      commentProse?.dispose();
      reviseMarks?.dispose();
      inlineDiff?.dispose();
      host?.dispose();
      offSelection();
      offSessionFeatures();
      for (const owner of commentOwners.values()) owner.dispose();
      commentOwners.clear();
    },
  };
}
