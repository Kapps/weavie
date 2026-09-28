// Renders a diff inside the live code editor (added-line decorations, removed-line ghost zones, char-level
// highlights, an action toolbar). The modified side is always the live model content, so the diff tracks edits
// live. Owns only its decorations/zones/widget — never disposes the host-owned live model.

import {
  DisposableStore,
  dispose,
  toDisposable,
} from "@codingame/monaco-vscode-api/vscode/vs/base/common/lifecycle";
import { type ClientSession, log } from "../bridge";
import { IS_MAC } from "../commands/keybindings";
import { onFontsChanged } from "../fonts";
import { monaco } from "./monaco-setup";
import type { DiffMarkers, HunkRevert, HunkUnkeep } from "./review/diff-markers";
import { addDiffZones, DIFF_RECOMPUTE_DEBOUNCE_MS } from "./review/diff-zones";
import type { ReviewCommentContext } from "./review/review-comment-session";
import { createReviewCommentView } from "./review/review-comment-view";
import { type ReviewDocument, ReviewDocumentScope } from "./review/review-document";
import type { ReviewActionPresentation } from "./review/review-file-actions";
import { buildReviewHunkControls } from "./review/review-hunk-controls";
import { hasFadedBand, reviewDiffSources } from "./review/review-sources";
import { createReviewToolbarPresenter } from "./review/review-toolbar-presenter";
import type { ReviewToolbarPaint, ReviewToolbarTarget } from "./review/review-toolbar-state";
import { sessionFileUri } from "./session-uri";

export type { HunkRevert, HunkUnkeep };

export type InlineDiffMode = "review" | "applied" | "view";

// Which scope the applied-review toolbar's Keep / Revert buttons act on; sticky across files (reset only on a
// turn-reset via clearAll).
type ReviewScope = "change" | "file" | "all";

/** The review scope stays shared across file and presentation switches. */
export interface ReviewScopeState {
  current: ReviewScope;
}

export interface InlineDiffOptions {
  /** The baseline/original text the live model is diffed against (the review baseline — the bright pending band). */
  original: string;
  /**
   * Applied mode — the accepted anchor (content at the last keep-all). The faded "accepted" band is
   * acceptedBaseline → original (kept-but-uncommitted hunks): they're rendered faded green, in place, with an
   * inline ↶ undo. Omitted or equal to `original` → no faded band.
   */
  acceptedBaseline?: string;
  /**
   * The content Claude produced. Live-model lines that differ from this are the user's own typing and render
   * fainter green. Omitted → no fade (every changed line is treated as Claude's).
   */
  claudeVersion?: string;
  /** review = pending openDiff proposal (Keep/Reject); applied = already-applied turn; view = read-only, no toolbar. */
  mode: InlineDiffMode;
  /** Review mode (openDiff) only: resolve the proposal as kept. */
  onAccept?: () => void;
  /** Review mode (openDiff) only: reject the proposal. */
  onReject?: () => void;
  /** Applied mode: revert the whole change set (the unbound whole-turn undo). */
  onUndo?: () => void;
  /**
   * Applied mode — per-hunk Keep: advance the host's review baseline over this hunk (no disk write) so it drops
   * from the pending diff for good. Same coordinates + guard as a revert.
   */
  onKeepHunk?: (hunk: HunkRevert) => void;
  /** Applied mode — per-hunk Revert: undo this hunk on disk (the host splices the baseline lines back). */
  onRevertHunk?: (hunk: HunkRevert) => void;
  /**
   * Applied mode — per-faded-hunk Un-keep: the host splices the accepted-anchor lines back into the review
   * baseline, so the kept hunk returns to the bright pending band (no disk write). Drives the inline ↶ undo.
   */
  onUnkeepHunk?: (hunk: HunkUnkeep) => void;
  /** Applied mode — accept all remaining changes and close the review. */
  onKeepAll?: () => void;
  /** The file walk is truncated, so whole-review actions would reach unseen files. */
  allActionsDisabled?: boolean;
  /** Applied mode — Keep-file: keep ALL of this file's changes, advancing its review baseline to current. */
  onKeepFile?: () => void;
  /** Applied mode — Revert-file: revert ALL of this file's changes to its turn baseline on disk. */
  onRevertFile?: () => void;
  /**
   * Applied review: step to the previous/next changed file in the set, landing on its first change. The
   * toolbar renders ← / → file buttons when supplied and there's more than one file. See docs/specs/turn-review.md.
   */
  onPrevFile?: () => void;
  onNextFile?: () => void;
  /** Applied review: this file's display name (the stacked label's filename) + its 1-based position in the set. */
  fileLabel?: string;
  fileIndex?: number;
  fileCount?: number;
  /** Applied review: names the review in the toolbar subtitle — "PR #12" or "vs main" ("diff against"). */
  reviewLabel?: string;
  /** Exact-session comments and draft authority, independent of this paint adapter. */
  commenting?: ReviewCommentContext;
}

/** Diff navigation and actions exposed to commands, keybindings, the palette, and Claude. */
export interface InlineDiffActions {
  nextChange(): boolean;
  prevChange(): boolean;
  /** Walk to the next / previous file in the post-turn review set (applied mode). */
  nextFile(): boolean;
  prevFile(): boolean;
  accept(): boolean;
  reject(): boolean;
  /** Revert the whole turn (revert all); confirms first. */
  undo(): boolean;
  /** Keep / revert every change in the active file (applied review); revertFile confirms first. */
  keepFile(): boolean;
  revertFile(): boolean;
  /** Keep the whole accumulated review set (applied review). */
  keepAll(): boolean;
  /** Comment on the current line (a PR file under review); false (key falls through) otherwise. */
  comment(): boolean;
  /** Undo the most recent keep / revert, or redo the last undone action; false when none. */
  undoKeep(): boolean;
  undoRevert(): boolean;
  redoReview(): boolean;
}

/** Presentation hooks keep review actions shared while each surface owns its toolbar and scrolling. */
export interface InlineDiffPresentation {
  scope: ReviewScopeState;
  active(): boolean;
  toolbarHost(): HTMLElement | null;
  revealLine(line: number): void;
  reviewLine(): number;
  prepareGeometry(markers: DiffMarkers | null): void;
  painted(): void;
  /** Keeps geometry-induced scroll changes inside the owning presentation. */
  updateGeometry(change: () => void): void;
}

/** Uses the cursor while it is visible; scrolling past it reviews the viewport's center. */
export function inlineReviewLine(editor: monaco.editor.IStandaloneCodeEditor): number {
  const cursor = editor.getPosition()?.lineNumber ?? 1;
  const ranges = editor.getVisibleRanges();
  if (
    ranges.length === 0 ||
    ranges.some((r) => r.startLineNumber <= cursor && cursor <= r.endLineNumber)
  )
    return cursor;
  return Math.round((ranges[0]!.startLineNumber + ranges[ranges.length - 1]!.endLineNumber) / 2);
}

/** Per-editor inline-diff controller. Diffs are keyed by file path; only the editor's current model renders. */
export interface InlineDiff {
  captureActions(): InlineDiffActions;
  /** Refresh the toolbar mount and command context after the active review surface changes. */
  refreshPresentation(): void;
  /** Register (or replace) the diff for a file path; renders immediately if that file is the active model. */
  set(session: ClientSession, path: string, options: InlineDiffOptions): void;
  /** Remove the diff for a file path. */
  clear(session: ClientSession, path: string): void;
  /** Register the diff keyed by an exact model URI string — for the transient `weavie-review:` review model. */
  setByUri(uri: string, options: InlineDiffOptions): void;
  /** Remove the diff registered by an exact model URI string (the review-model counterpart of clear). */
  clearByUri(uri: string): void;
  /** Reconcile the applied-review paths for one exact owner without disturbing retained paint. */
  retainApplied(session: ClientSession, paths: readonly string[]): void;
  /** Remove every registered diff. */
  clearAll(): void;
  /** Whether a diff is registered for an exact model URI string (so other features can suspend over it). */
  hasDiffForUri(uri: string): boolean;
  /** Bind the session-global undo/redo handlers (set once; review undo/redo isn't tied to a file). */
  bindHistory(handlers: ReviewHistoryHandlers): void;
  /** Update the review undo/redo availability (host-pushed) so the toolbar + chords reflect it. */
  setReviewHistory(state: ReviewHistoryState): void;
  /** Set (or clear) the parked-navigator summary; it surfaces when no changed file is in view. */
  setParkedReview(summary: ParkedReview | undefined): void;
  /** Tear down listeners + any rendered markers (never disposes a model). */
  dispose(): void;
}

/** The parked-navigator summary: how many files are pending review, and how to step into the first change. */
export interface ParkedReview {
  fileCount: number;
  /** Names the review in the parked subtitle ("PR #12", "vs main"); absent for the post-turn set. */
  label?: string;
  stepIn: () => void;
  nextFile: () => void;
  prevFile: () => void;
}

/** Session-global undo/redo handlers — review history isn't per-file, so these are bound once. */
export interface ReviewHistoryHandlers {
  onUndoKeep: () => void;
  onUndoRevert: () => void;
  onUndoLast: () => void;
  onRedo: () => void;
}

/** Host-pushed review undo/redo availability (`canUndo` is "either kind"). */
export interface ReviewHistoryState {
  canUndo: boolean;
  canUndoKeep: boolean;
  canUndoRevert: boolean;
  canRedo: boolean;
}

/** Creates an inline-diff controller bound to `editor`. */
export function createInlineDiff(
  editor: monaco.editor.IStandaloneCodeEditor,
  presentation: InlineDiffPresentation,
): InlineDiff {
  const documents = new ReviewDocumentScope();
  const inline = createSharedInlineDiff(editor, presentation, documents);
  return {
    ...inline,
    dispose: () => {
      inline.dispose();
      documents.dispose();
    },
  };
}

/** Shared documents can outlive this surface's Monaco binding. */
export function createSharedInlineDiff(
  editor: monaco.editor.IStandaloneCodeEditor,
  presentation: InlineDiffPresentation,
  documents: ReviewDocumentScope,
): InlineDiff {
  let target: ReviewToolbarTarget = { kind: "none" };
  let parked: ParkedReview | undefined;
  let history: ReviewHistoryState = {
    canUndo: false,
    canUndoKeep: false,
    canUndoRevert: false,
    canRedo: false,
  };
  let handlers: ReviewHistoryHandlers | undefined;
  const toolbar = createReviewToolbarPresenter({
    host: presentation.toolbarHost,
    active: presentation.active,
    scope: presentation.scope,
    target: () =>
      target.kind !== "none"
        ? target
        : parked !== undefined && parked.fileCount > 0
          ? { kind: "parked", summary: parked }
          : { kind: "none" },
    reviewPending: () => parked !== undefined,
    composerFocused: () => paint.composerFocused(),
    history: () => history,
    historyHandlers: () => handlers,
  });
  const paint = createInlineDiffPaint(editor, presentation, documents, (value) => {
    target = value;
    toolbar.refresh();
  });
  return {
    ...paint,
    captureActions: toolbar.captureActions,
    refreshPresentation: toolbar.refresh,
    bindHistory: (value) => {
      handlers = value;
    },
    setReviewHistory: (value) => {
      history = value;
      toolbar.refresh();
    },
    setParkedReview: (value) => {
      parked = value;
      paint.refresh();
    },
    clearAll: () => {
      parked = undefined;
      presentation.scope.current = "change";
      toolbar.reset();
      paint.clearAll();
    },
    dispose: () => {
      toolbar.dispose();
      paint.dispose();
    },
  };
}

export type InlineDiffPaint = Pick<
  InlineDiff,
  | "set"
  | "clear"
  | "setByUri"
  | "clearByUri"
  | "retainApplied"
  | "clearAll"
  | "hasDiffForUri"
  | "dispose"
> & {
  refresh(): void;
  composerFocused(): boolean;
  commentsRetained(): boolean;
};

export type InlineDiffPaintPresentation = Pick<
  InlineDiffPresentation,
  "scope" | "revealLine" | "reviewLine" | "prepareGeometry" | "painted" | "updateGeometry"
>;

/** An ordinary editor owns its comment binding; unified files supply their stable presenter. */
export function createInlineDiffPaint(
  editor: monaco.editor.IStandaloneCodeEditor,
  presentation: InlineDiffPaintPresentation,
  documents: ReviewDocumentScope,
  onTargetChanged: (target: ReviewToolbarTarget) => void,
): InlineDiffPaint {
  const owned = new DisposableStore();
  const comments = owned.add(createReviewCommentView(editor, presentation.updateGeometry));
  const configure = (): void => {
    const model = editor.getModel();
    comments.configure(
      model === null ? undefined : documents.get(model.uri.toString())?.commenting,
    );
  };
  try {
    owned.add(editor.onDidChangeModel(configure));
    owned.add(documents.onDidChangeConfiguration(configure));
    configure();
    const paint = createReviewDiffPaint(editor, presentation, documents, comments, onTargetChanged);
    return { ...paint, dispose: () => dispose([paint, owned]) };
  } catch (error) {
    try {
      owned.dispose();
    } catch (cleanup) {
      throw new AggregateError([error, cleanup], "Inline comment binding and cleanup failed");
    }
    throw error;
  }
}

/** Paint adapters publish their actual displayed target; they never own a surface toolbar. */
export function createReviewDiffPaint(
  editor: monaco.editor.IStandaloneCodeEditor,
  presentation: InlineDiffPaintPresentation,
  documents: ReviewDocumentScope,
  comments: Pick<ReturnType<typeof createReviewCommentView>, "open" | "focused" | "retained">,
  onTargetChanged: (target: ReviewToolbarTarget) => void,
): InlineDiffPaint {
  const appliedKeys = new Map<ClientSession, Set<string>>();
  let documentBinding: { document: ReviewDocument; lease: monaco.IDisposable } | undefined;
  const releaseDocument = (): void => {
    const previous = documentBinding;
    documentBinding = undefined;
    previous?.lease.dispose();
  };
  const bindDocument = (model: monaco.editor.ITextModel): ReviewDocument => {
    if (documentBinding?.document !== documents.forModel(model)) {
      releaseDocument();
      const document = documents.forModel(model);
      documentBinding = { document, lease: document.retainSources() };
    }
    return documentBinding.document;
  };
  let decorations: monaco.editor.IEditorDecorationsCollection | undefined;
  let zoneIds: string[] = [];
  let renderedUri: string | undefined;
  let recomputeTimer: ReturnType<typeof setTimeout> | undefined;
  let renderGeneration = 0;
  let presentationRevision = 0;
  let renderInFlight = false;
  let renderQueued = false;
  let disposed = false;
  const initialProposalReveals = new Set<string>();
  let toolbarPaint: ReviewToolbarPaint = { status: "pending" };
  // Monaco content widgets for the per-hunk inline affordances — ✓ keep / ✕ revert beside each bright pending
  // hunk, ↶ undo beside each faded accepted one — removed on every re-render.
  let hunkWidgets: monaco.editor.IContentWidget[] = [];
  // Every control whose action names a specific hunk, with the title it carries when it can act.
  let scopeActions: { button: HTMLButtonElement; title: string }[] = [];

  const clearControls = (): void => {
    presentationRevision++;
    for (const widget of hunkWidgets) {
      editor.removeContentWidget(widget);
    }
    hunkWidgets = [];
    scopeActions = [];
    toolbarPaint = { status: "pending" };
  };

  const changeViewZones = (change: Parameters<typeof editor.changeViewZones>[0]): void =>
    presentation.updateGeometry(() => editor.changeViewZones(change));

  const clearPaint = (): void => {
    decorations?.clear();
    decorations = undefined;
    if (zoneIds.length > 0) {
      changeViewZones((accessor) => {
        for (const id of zoneIds) {
          accessor.removeZone(id);
        }
      });
      zoneIds = [];
    }
  };

  const clearRenderState = (): void => {
    clearPaint();
    clearControls();
    renderedUri = undefined;
  };
  const clearRender = (): void => {
    clearRenderState();
    publishTarget();
  };

  // A content widget hugging a hunk's first line, anchored EXACT at the line's end so it sits beside the code.
  const anchoredWidget = (
    id: string,
    model: monaco.editor.ITextModel,
    anchorLine: number,
    dom: HTMLElement,
  ): monaco.editor.IContentWidget => {
    const line = anchorLine;
    return {
      getId: () => `${id}.${line}`,
      getDomNode: () => dom,
      getPosition: () => ({
        position: { lineNumber: line, column: model.getLineMaxColumn(line) },
        preference: [monaco.editor.ContentWidgetPositionPreference.EXACT],
        // Model-to-view conversion needs Right to include text injected at the anchor.
        positionAffinity: monaco.editor.PositionAffinity.Right,
      }),
    };
  };

  const reveal = (line: number): void => {
    editor.setPosition({ lineNumber: line, column: 1 });
    editor.focus();
    presentation.revealLine(line);
  };

  const reviewLine = presentation.reviewLine;

  const actionPresentation = (): ReviewActionPresentation => {
    const binding = documentBinding!;
    const revision = presentationRevision;
    return {
      scope: presentation.scope,
      valid: () =>
        !disposed &&
        presentationRevision === revision &&
        documentBinding === binding &&
        editor.getModel() === binding.document.model,
      availability: () => toolbarPaint.status,
      reviewLine,
      commentLine: () => editor.getPosition()?.lineNumber ?? 1,
      revealLine: reveal,
      selectLine: (line) => editor.setPosition({ lineNumber: line, column: 1 }),
      openComment: (line) => comments.open(line),
      composerFocused,
      swallowFileNavigation: IS_MAC,
    };
  };
  // A per-hunk action needs coordinates that still match the live model, and they only do at the version the
  // render was computed against — an edit since then (a keystroke while the recompute is in flight) moves
  // every hunk after it, so keeping or reverting would act on the wrong lines.
  const geometryStale = (): boolean =>
    toolbarPaint.status === "ready" &&
    (documentBinding === undefined ||
      documentBinding.document.actions.stale ||
      documentBinding.document.actions.options !== toolbarPaint.options ||
      documentBinding.document.actions.geometry?.markers !== toolbarPaint.markers);

  const STALE_TITLE = "Waiting for the edited file's change geometry";

  // Dim every per-hunk control while its coordinates are a version behind, and say why — the chords swallow
  // themselves in that window, so the reason belongs where the user meets it.
  const syncScopeButtons = (): void => {
    const blocked = geometryStale();
    for (const { button, title } of scopeActions) {
      button.disabled = blocked;
      button.title = blocked ? STALE_TITLE : title;
    }
  };

  const trackScopeAction = (button: HTMLButtonElement): HTMLButtonElement => {
    scopeActions.push({ button, title: button.title });
    return button;
  };

  const composerFocused = comments.focused;
  const toolbarTarget = (): ReviewToolbarTarget => {
    const binding = documentBinding;
    if (
      !disposed &&
      binding !== undefined &&
      binding.document.model === editor.getModel() &&
      documents.has(binding.document.model.uri.toString())
    ) {
      return {
        kind: "file",
        owner: binding,
        document: binding.document,
        presentation: actionPresentation(),
        paint: toolbarPaint,
      };
    }
    return { kind: "none" };
  };
  const publishTarget = (): void => onTargetChanged(toolbarTarget());

  const renderUnavailable = (
    uriString: string,
    options: InlineDiffOptions,
    message: string,
  ): void => {
    presentation.updateGeometry(() => {
      clearRenderState();
      toolbarPaint = { status: "unavailable", options, message };
      presentation.prepareGeometry(null);
    });
    renderedUri = uriString;
    publishTarget();
    presentation.painted();
  };

  const replacePaint = (markers: DiffMarkers): void => {
    if (decorations === undefined) {
      decorations = editor.createDecorationsCollection(markers.decorations);
    } else {
      decorations.set(markers.decorations);
    }

    const previousZoneIds = zoneIds;
    changeViewZones((accessor) => {
      for (const id of previousZoneIds) accessor.removeZone(id);
      zoneIds = addDiffZones(editor, accessor, markers);
    });
  };

  const render = async (uriString: string, generation: number): Promise<void> => {
    const model = editor.getModel();
    if (model === null || model.uri.toString() !== uriString) {
      return;
    }
    const options = documents.get(uriString);
    if (options === undefined) {
      return;
    }
    const version = model.getVersionId();
    const computation = bindDocument(model).prepare(reviewDiffSources(options));
    const calculation = computation instanceof Promise ? await computation : computation;
    if (
      disposed ||
      generation !== renderGeneration ||
      editor.getModel() !== model ||
      model.getVersionId() !== version ||
      documents.get(uriString) !== options
    ) {
      return;
    }
    if (calculation.status === "timed-out") {
      renderUnavailable(uriString, options, "Diff calculation timed out");
      return;
    }
    if (calculation.status === "failed") {
      log("error", `inline diff calculation failed: ${String(calculation.error)}`);
      renderUnavailable(uriString, options, "Diff calculation failed");
      return;
    }

    let initialLine: number | undefined;
    presentation.updateGeometry(() => {
      clearControls();

      const markers = calculation.markers;
      // A fully-kept file has no bright (pending) hunks but still carries a faded accepted band — don't bail on it.
      if (markers.hunks.length === 0 && !hasFadedBand(options)) {
        clearRender();
        presentation.prepareGeometry(markers);
        initialProposalReveals.delete(uriString);
        return; // no net change and nothing kept — nothing to render
      }
      const { hunks } = markers;

      replacePaint(markers);

      toolbarPaint = { status: "ready", options, markers };
      if (initialProposalReveals.delete(uriString) && hunks[0] !== undefined) {
        initialLine = hunks[0].anchorLine;
      }
      for (const control of buildReviewHunkControls(
        documentBinding!.document,
        actionPresentation(),
        markers,
        trackScopeAction,
      )) {
        const widget = anchoredWidget(control.id, model, control.line, control.element);
        hunkWidgets.push(widget);
        editor.addContentWidget(widget);
      }
      renderedUri = uriString;
      presentation.prepareGeometry(markers);
    });
    publishTarget();
    presentation.painted();
    if (initialLine !== undefined) {
      editor.setPosition({ lineNumber: initialLine, column: 1 });
      presentation.revealLine(initialLine);
    }
  };

  const drainRender = async (): Promise<void> => {
    if (renderInFlight || disposed) {
      return;
    }
    renderInFlight = true;
    try {
      while (renderQueued && !disposed) {
        renderQueued = false;
        const model = editor.getModel();
        const uriString = model?.uri.toString();
        const options = uriString === undefined ? undefined : documents.get(uriString);
        if (model === null || uriString === undefined || options === undefined) {
          continue;
        }
        const generation = renderGeneration;
        try {
          await render(uriString, generation);
        } catch (error) {
          if (
            !disposed &&
            generation === renderGeneration &&
            editor.getModel() === model &&
            documents.get(uriString) === options
          ) {
            log("error", `inline diff rendering failed: ${String(error)}`);
            renderUnavailable(uriString, options, "Diff calculation failed");
          }
        }
      }
    } finally {
      renderInFlight = false;
      if (renderQueued && !disposed) {
        void drainRender();
      }
    }
  };

  const queueRender = (): void => {
    renderQueued = true;
    void drainRender();
  };

  // Render the active model's diff if it has one; else park the navigator when a review set is pending; else clear.
  const renderActive = (): void => {
    renderGeneration++;
    const model = editor.getModel();
    const uriString = model?.uri.toString();
    const options = uriString === undefined ? undefined : documents.get(uriString);
    if (model !== null && uriString !== undefined && options !== undefined) {
      if (renderedUri !== uriString) {
        clearRender(); // what is on screen belongs to another file; nothing here to preserve
      }
      bindDocument(model);
      publishTarget();
      queueRender();
    } else {
      renderQueued = false;
      releaseDocument();
      clearRender();
    }
  };

  const scheduleRender = (): void => {
    const model = editor.getModel();
    const uriString = model?.uri.toString();
    const options = uriString === undefined ? undefined : documents.get(uriString);
    if (model === null || uriString === undefined || options === undefined) {
      return;
    }
    initialProposalReveals.delete(uriString);
    renderGeneration++;
    syncScopeButtons();
    publishTarget();
    if (recomputeTimer !== undefined) {
      clearTimeout(recomputeTimer);
    }
    recomputeTimer = setTimeout(() => {
      recomputeTimer = undefined;
      queueRender();
    }, DIFF_RECOMPUTE_DEBOUNCE_MS);
  };

  const subscriptions = new DisposableStore();
  const release = (): void => {
    if (disposed) return;
    disposed = true;
    renderGeneration++;
    renderQueued = false;
    initialProposalReveals.clear();
    if (recomputeTimer !== undefined) clearTimeout(recomputeTimer);
    dispose([
      subscriptions,
      toDisposable(releaseDocument),
      toDisposable(clearPaint),
      toDisposable(clearControls),
      toDisposable(() => {
        renderedUri = undefined;
        publishTarget();
      }),
    ]);
  };
  try {
    // View zones are lost on model swap — close any open composer (its zone is gone) and re-render the new model.
    subscriptions.add(
      editor.onDidChangeModel(() => {
        releaseDocument();
        onTargetChanged({ kind: "none" });
        if (recomputeTimer !== undefined) {
          clearTimeout(recomputeTimer);
          recomputeTimer = undefined;
        }
        renderActive();
      }),
    );
    subscriptions.add(editor.onDidChangeModelContent(scheduleRender));
    subscriptions.add(toDisposable(onFontsChanged(renderActive)));
    // Live-update the applied toolbar's change counter + dots as the cursor walks hunks (no full re-render).
    subscriptions.add(
      editor.onDidChangeCursorPosition(() => {
        const uri = editor.getModel()?.uri.toString();
        if (uri !== undefined) {
          initialProposalReveals.delete(uri);
        }
        publishTarget();
      }),
    );
    // Manual scrolling moves the review position too (reviewLine follows the viewport once the cursor leaves
    // it), so the counter tracks scroll as well as cursor moves.
    subscriptions.add(editor.onDidScrollChange(publishTarget));

    // Register/remove a diff keyed by an exact model URI string (the path-based set/clear convert a file path
    // to its file:// URI; the review path passes the transient model's URI).
    const setByUri = (key: string, options: InlineDiffOptions): void => {
      if (!documents.has(key) && options.mode === "review") {
        initialProposalReveals.add(key);
      }
      documents.configure(key, options);
    };
    const clearByUri = (key: string): void => {
      for (const [session, keys] of appliedKeys) {
        keys.delete(key);
        if (keys.size === 0) {
          appliedKeys.delete(session);
        }
      }
      documents.configure(key, undefined);
    };

    subscriptions.add(
      documents.onDidChangeConfiguration((uri) => {
        if (!documents.has(uri)) {
          initialProposalReveals.delete(uri);
          if (documentBinding?.document.model.uri.toString() === uri) releaseDocument();
        }
        if (editor.getModel()?.uri.toString() !== uri) return;
        syncScopeButtons();
        publishTarget();
        // Autosave pushes share the edit's pending debounce; explicit review changes paint immediately.
        if (!documents.has(uri) || renderedUri !== uri || recomputeTimer === undefined)
          renderActive();
      }),
    );
    renderActive();

    return {
      refresh: renderActive,
      composerFocused,
      commentsRetained: comments.retained,
      set(session, path, options) {
        const key = sessionFileUri(session, path).toString();
        let keys = appliedKeys.get(session);
        if (keys === undefined) {
          keys = new Set<string>();
          appliedKeys.set(session, keys);
        }
        keys.add(key);
        setByUri(key, options);
      },
      clear(session, path) {
        clearByUri(sessionFileUri(session, path).toString());
      },
      setByUri,
      clearByUri,
      retainApplied(session, paths) {
        const keys = appliedKeys.get(session);
        if (keys === undefined) {
          return;
        }
        const retained = new Set(paths.map((path) => sessionFileUri(session, path).toString()));
        for (const key of keys) {
          if (retained.has(key)) {
            continue;
          }
          keys.delete(key);
          documents.configure(key, undefined);
        }
        if (keys.size === 0) {
          appliedKeys.delete(session);
        }
      },
      clearAll() {
        releaseDocument();
        appliedKeys.clear();
        renderGeneration++;
        renderQueued = false;
        initialProposalReveals.clear();
        documents.retain(() => false);
        clearRender();
        publishTarget();
      },
      hasDiffForUri: (uri) => documents.has(uri),
      dispose: release,
    };
  } catch (error) {
    try {
      release();
    } catch (cleanupError) {
      throw new AggregateError(
        [error, cleanupError],
        "Inline diff construction and cleanup failed",
      );
    }
    throw error;
  }
}
