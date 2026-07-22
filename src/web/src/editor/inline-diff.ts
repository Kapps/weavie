// Renders a diff inside the live code editor (added-line decorations, removed-line ghost zones, char-level
// highlights, an action toolbar). The modified side is always the live model content, so the diff tracks edits
// live. Owns only its decorations/zones/widget — never disposes the host-owned live model.

import { linesDiffComputers } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/diff/linesDiffComputers";
import type { ReviewCommentInfo } from "../bridge";
import { setContext } from "../commands/context";
import { formatKey, IS_MAC } from "../commands/keybindings";
import { findCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { onFontsChanged } from "../fonts";
import { reviewToModelLine } from "./diff-geometry";
import { canonicalFsPath } from "./fs-path";
import { monaco } from "./monaco-setup";

const DIFF_OPTIONS = {
  ignoreTrimWhitespace: false,
  maxComputationTimeMs: 1000,
  computeMoves: false,
} as const;

// Debounce diff recompute so typing into a model under review (or a burst of working-copy reloads) doesn't
// recompute + re-lay-out view zones on every keystroke.
const RECOMPUTE_DEBOUNCE_MS = 120;

// Show change-position dots only up to this many hunks; above it the numeric `change j/M` carries position.
const MAX_CHANGE_DOTS = 7;

// Height of the "New file" header band shown above a wholly-new file's first line.
const NEW_FILE_BADGE_HEIGHT = 24;

export type InlineDiffMode = "review" | "applied" | "view";

// Which scope the applied-review toolbar's Keep / Revert buttons act on; sticky across files (reset only on a
// turn-reset via clearAll).
type ReviewScope = "change" | "file" | "all";

/**
 * Coordinates + concurrency guard for reverting one hunk on disk. Ranges are 1-based, end-exclusive;
 * `guardText` is the current text the web sees — the host aborts if the file's current lines differ.
 */
export interface HunkRevert {
  baselineStart: number;
  baselineEndExclusive: number;
  currentStart: number;
  currentEndExclusive: number;
  guardText: string;
}

/**
 * Coordinates + concurrency guards for un-keeping one faded (accepted) hunk. `accepted*` is its range in the
 * accepted anchor (the lines spliced back); `review*` its range in the review baseline (the splice target).
 * Both sides are guarded with the text the web rendered — `guardText` (review baseline) and
 * `acceptedGuardText` (accepted anchor) — so the host aborts if either moved (a concurrent keep, or a turn
 * boundary committing the anchor) instead of splicing lines the user never saw.
 */
export interface HunkUnkeep {
  acceptedStart: number;
  acceptedEndExclusive: number;
  reviewStart: number;
  reviewEndExclusive: number;
  acceptedGuardText: string;
  guardText: string;
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
  /** Applied mode — Keep-all: clear the whole accumulated review set in one action. */
  onKeepAll?: () => void;
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
  /** A PR file's review comments anchored to its lines, rendered as threads below their line (applied mode). */
  comments?: ReviewCommentInfo[];
  /** Applied mode (PR file): post a new comment on `line` (the current side). */
  onAddComment?: (line: number, body: string) => void;
  /** Applied mode (PR file): reply to the thread rooted at `inReplyTo`. */
  onReply?: (inReplyTo: number, body: string) => void;
}

/** Per-editor inline-diff controller. Diffs are keyed by file path; only the editor's current model renders. */
export interface InlineDiff {
  /** Register (or replace) the diff for a file path; renders immediately if that file is the active model. */
  set(path: string, options: InlineDiffOptions): void;
  /** Remove the diff for a file path. */
  clear(path: string): void;
  /** Register the diff keyed by an exact model URI string — for the transient `weavie-review:` review model. */
  setByUri(uri: string, options: InlineDiffOptions): void;
  /** Remove the diff registered by an exact model URI string (the review-model counterpart of clear). */
  clearByUri(uri: string): void;
  /** Remove every registered diff. */
  clearAll(): void;
  /** Whether a diff is registered for an exact model URI string (so other features can suspend over it). */
  hasDiffForUri(uri: string): boolean;
  // The nav/action methods return whether they handled the key, so an unmatched keybinding falls through to the editor.
  /** Jump to the next change hunk in the active diff. */
  nextChange(): boolean;
  /** Jump to the previous change hunk in the active diff. */
  prevChange(): boolean;
  /** Walk to the next file in the review set (applied mode); false when there's nothing to step to. */
  nextFile(): boolean;
  /** Walk to the previous file in the review set (applied mode); false when there's nothing to step to. */
  prevFile(): boolean;
  /** Keep at the toolbar's current scope (applied review), or accept the openDiff proposal (review mode). */
  accept(): boolean;
  /** Revert at the toolbar's current scope (applied review), or reject the openDiff proposal (review mode). */
  reject(): boolean;
  /** Undo the active applied turn (revert all). */
  undo(): boolean;
  /** Keep every change in the active file (applied review): advance its review baseline to current. */
  keepFile(): boolean;
  /** Revert every change in the active file on disk (applied review). */
  revertFile(): boolean;
  /** Keep the whole accumulated review set in one action (applied review). */
  keepAll(): boolean;
  /** Comment on the current line (a PR file under review); false (key falls through) otherwise. */
  comment(): boolean;
  /** Undo the most recent keep; false (key falls through) when there's none. */
  undoKeep(): boolean;
  /** Undo the most recent revert; false (key falls through) when there's none. */
  undoRevert(): boolean;
  /** Redo the most recently undone review action; false when there's none. */
  redoReview(): boolean;
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

// Split a string into lines the way a Monaco model does (so `original` lines line up with getLinesContent()).
function splitLines(text: string): string[] {
  return text.replace(/\r\n?/g, "\n").split("\n");
}

// A file carries a faded "accepted" band (kept-but-uncommitted hunks) iff its accepted anchor diverges from the
// review baseline. Only meaningful in applied mode. A fully-kept file has no bright hunks but still shows this band.
function hasFadedBand(options: InlineDiffOptions): boolean {
  return (
    options.mode === "applied" &&
    options.acceptedBaseline !== undefined &&
    options.acceptedBaseline !== options.original
  );
}

// One change hunk: the line coordinates a keep/revert needs. anchorLine is the modified-side line to reveal.
interface Hunk {
  anchorLine: number;
  baselineStart: number;
  baselineEndExclusive: number;
  currentStart: number;
  currentEndExclusive: number;
}

// One faded (accepted) hunk: where it sits in the live model (anchorLine) plus the coordinates an un-keep needs.
interface AcceptedHunk extends HunkUnkeep {
  anchorLine: number;
}

/**
 * The 1-based modified-side line of the first change between `original` and `modified`, or 1 when identical.
 * Uses the same diff machinery and anchor rule as `render`, so it matches where "next change" first lands.
 */
export function firstChangedLine(original: string, modified: string): number {
  const { changes } = linesDiffComputers
    .getDefault()
    .computeDiff(splitLines(original), splitLines(modified), DIFF_OPTIONS);
  const first = changes[0];
  return first === undefined ? 1 : Math.max(1, first.modified.startLineNumber);
}

/** Creates an inline-diff controller bound to `editor`. */
export function createInlineDiff(editor: monaco.editor.IStandaloneCodeEditor): InlineDiff {
  const diffs = new Map<string, InlineDiffOptions>();
  let decorations: monaco.editor.IEditorDecorationsCollection | undefined;
  let zoneIds: string[] = [];
  // The floating action bar is a plain DOM child of the editor (not a Monaco overlay widget) so it sits above
  // sticky-scroll and clear of the minimap, positioned bottom-center via CSS.
  let toolbarNode: HTMLElement | undefined;
  let renderedUri: string | undefined;
  let recomputeTimer: ReturnType<typeof setTimeout> | undefined;
  // The currently-rendered diff's options + hunks; the nav/action methods all operate on these.
  let currentOptions: InlineDiffOptions | undefined;
  let currentHunks: Hunk[] = [];
  // Monaco content widgets for the per-hunk inline affordances — ✓ keep / ✕ revert beside each bright pending
  // hunk, ↶ undo beside each faded accepted one — removed on every re-render.
  let hunkWidgets: monaco.editor.IContentWidget[] = [];
  // Which scope the Keep / Revert buttons act on; sticky across file switches, reset only on clearAll.
  let currentScope: ReviewScope = "change";
  // Live-updated applied-toolbar bits: the `file i/N · change j/M` subtitle + change dots, plus the scope
  // dropdown nodes (kept so one document listener can close it on an outside click).
  let counterNode: HTMLElement | undefined;
  let dotsNode: HTMLElement | undefined;
  let scopeMenuNode: HTMLElement | undefined;
  let scopeWrapNode: HTMLElement | undefined;
  // Session-global review undo/redo: availability (host-pushed) + the bound handlers, plus the toolbar buttons
  // that reflect it. Not per-file, so it survives the active diff clearing.
  let history: ReviewHistoryState = {
    canUndo: false,
    canUndoKeep: false,
    canUndoRevert: false,
    canRedo: false,
  };
  let historyHandlers: ReviewHistoryHandlers | undefined;
  let undoButton: HTMLButtonElement | undefined;
  let redoButton: HTMLButtonElement | undefined;
  // The parked-navigator summary (review set non-empty, no changed file in view) + whether it's the rendered
  // surface right now, so the nav/Keep keys step in instead of acting on a (nonexistent) hunk.
  let parkedReview: ParkedReview | undefined;
  let showingParked = false;
  // The transient "new comment" composer zone (a PR file under review), opened by the toolbar Comment button.
  // Closed on submit/cancel, a model swap (onModel), and clearAll — but NOT on a routine same-model re-render
  // (that would wipe a half-typed comment), so it survives a keep/faded-band/diff re-push while composing.
  let composerZoneId: string | undefined;
  // How many composer textareas (the new-comment composer AND every thread's reply composer) currently hold
  // focus, tracked by focus/blur since the editor's shadow root hides the real activeElement. A live composer
  // makes the review chords fall through to it — so Ctrl+Enter submits the comment instead of Keeping a hunk,
  // and Ctrl+Backspace deletes a word instead of Reverting one on disk.
  let focusedComposers = 0;
  // Content observers for the sized zones (threads in zoneIds, plus the new-comment composer's own),
  // disconnected when their zone is removed.
  let zoneObservers: ResizeObserver[] = [];
  let composerObserver: ResizeObserver | undefined;

  const clearRender = (): void => {
    decorations?.clear();
    decorations = undefined;
    // NB: a new-comment composer is deliberately NOT closed here — a routine same-model re-render (a keep, the
    // faded band, a fresh diff push) would otherwise wipe the half-typed comment. It's closed on submit/cancel,
    // a model swap (onModel), and clearAll instead.
    if (zoneIds.length > 0) {
      editor.changeViewZones((accessor) => {
        for (const id of zoneIds) {
          accessor.removeZone(id);
        }
      });
      zoneIds = [];
      // The reply composers lived in those zones — removing a focused one may not fire blur, so clear their
      // focus tally here. The new-comment composer (not in zoneIds) survives and stays gated by composerZoneId.
      focusedComposers = 0;
    }
    for (const observer of zoneObservers) {
      observer.disconnect();
    }
    zoneObservers = [];
    for (const widget of hunkWidgets) {
      editor.removeContentWidget(widget);
    }
    hunkWidgets = [];
    toolbarNode?.remove();
    toolbarNode = undefined;
    counterNode = undefined;
    dotsNode = undefined;
    scopeMenuNode = undefined;
    scopeWrapNode = undefined;
    undoButton = undefined;
    redoButton = undefined;

    showingParked = false;
    currentOptions = undefined;
    currentHunks = [];
    renderedUri = undefined;
  };

  const buildGhost = (lines: string[], faded: boolean): HTMLElement => {
    const node = document.createElement("div");
    // Faded variant: a removed line in an already-accepted hunk, dimmed to match its faded green counterpart.
    node.className = faded
      ? "weavie-inline-removed weavie-inline-removed-faded"
      : "weavie-inline-removed";
    // Use the resolved metrics, not the raw font setting: the view zone reserves `lines.length * lineHeight`
    // px, so the ghost rows must use that same line height or they overflow the zone.
    const fontInfo = editor.getOption(monaco.editor.EditorOption.fontInfo);
    node.style.fontFamily = fontInfo.fontFamily;
    node.style.fontSize = `${fontInfo.fontSize}px`;
    node.style.lineHeight = `${fontInfo.lineHeight}px`;
    // Render tabs at the editor's tab width so a removed line's leading indentation lines up with the live
    // code, instead of CSS `tab-size`'s default of 8.
    node.style.tabSize = String(editor.getModel()?.getOptions().tabSize ?? 4);
    for (const line of lines) {
      const row = document.createElement("div");
      row.className = "weavie-inline-removed-line";
      row.textContent = line.length === 0 ? " " : line;
      node.appendChild(row);
    }
    return node;
  };

  // The "New file" header band: a sans-serif green pill above a wholly-new file's first line, so an all-added
  // file is labelled once instead of washed green on every line.
  const buildNewFileBadge = (): HTMLElement => {
    const node = document.createElement("div");
    node.className = "weavie-inline-newfile";
    const tag = document.createElement("span");
    tag.className = "weavie-inline-newfile-tag";
    tag.textContent = "New file";
    node.appendChild(tag);
    return node;
  };

  // A comment composer: a textarea + a submit button. onSubmit fires with the trimmed body (ignored when empty);
  // Ctrl/Cmd+Enter submits too. Used for both a new comment and a thread reply.
  const buildComposer = (
    placeholder: string,
    submitLabel: string,
    onSubmit: (body: string) => void,
  ): HTMLElement => {
    const wrap = document.createElement("div");
    wrap.className = "weavie-pr-composer";
    const input = document.createElement("textarea");
    input.className = "weavie-pr-composer-input";
    input.placeholder = placeholder;
    input.rows = 2;
    // Track focus so composerFocused() covers every composer (new comment + each reply), not just the new-comment
    // zone — else a review chord typed into a reply would Keep/Revert a hunk instead of reaching the textarea.
    input.addEventListener("focus", () => {
      focusedComposers++;
    });
    input.addEventListener("blur", () => {
      focusedComposers = Math.max(0, focusedComposers - 1);
    });
    const submit = (): void => {
      const body = input.value.trim();
      if (body.length > 0) {
        onSubmit(body);
        input.value = "";
      }
    };
    input.addEventListener("keydown", (event) => {
      if (event.key === "Enter" && (event.metaKey || event.ctrlKey)) {
        event.preventDefault();
        submit();
      }
    });
    const button = document.createElement("button");
    button.type = "button";
    button.className = "weavie-pr-composer-submit";
    button.textContent = submitLabel;
    button.addEventListener("click", submit);
    wrap.append(input, button);
    return wrap;
  };

  // A view zone sized by its content. Monaco force-sets the zone node's own height, so `content` lives inside a
  // bare wrapper and its measured height (margins included) drives heightInPx via layoutZone; the ResizeObserver
  // keeps the zone in sync as comment bodies wrap on editor resize.
  const addContentSizedZone = (
    accessor: monaco.editor.IViewZoneChangeAccessor,
    afterLineNumber: number,
    content: HTMLElement,
  ): { id: string; observer: ResizeObserver } => {
    const domNode = document.createElement("div");
    domNode.appendChild(content);
    const zone: monaco.editor.IViewZone & { heightInPx: number } = {
      afterLineNumber,
      heightInPx: 0,
      domNode,
    };
    const id = accessor.addZone(zone);
    const observer = new ResizeObserver(() => {
      const style = getComputedStyle(content);
      const height =
        content.offsetHeight + parseFloat(style.marginTop) + parseFloat(style.marginBottom);
      if (height > 0 && Math.abs(height - zone.heightInPx) >= 1) {
        zone.heightInPx = height;
        editor.changeViewZones((a) => a.layoutZone(id));
      }
    });
    observer.observe(content);
    return { id, observer };
  };

  // A comment thread for one line: its comments (root + replies, in order) and a reply composer. The reply posts
  // against the thread's root id (onReply); the host re-fetches and re-renders.
  const buildCommentThread = (
    comments: ReviewCommentInfo[],
    options: InlineDiffOptions,
  ): HTMLElement => {
    const node = document.createElement("div");
    node.className = "weavie-pr-thread";
    const rootId = comments.find((c) => c.inReplyTo === 0)?.id ?? comments[0]?.id ?? 0;
    for (const comment of comments) {
      const item = document.createElement("div");
      item.className = "weavie-pr-comment";
      const author = document.createElement("span");
      author.className = "weavie-pr-comment-author";
      author.textContent = `@${comment.author}`;
      const body = document.createElement("span");
      body.className = "weavie-pr-comment-body";
      body.textContent = comment.body;
      item.append(author, body);
      node.appendChild(item);
    }
    if (options.onReply !== undefined) {
      const onReply = options.onReply;
      node.appendChild(buildComposer("Reply…", "Reply", (text) => onReply(rootId, text)));
    }
    return node;
  };

  // Remove the transient new-comment composer zone, if one is open.
  const closeNewComposer = (): void => {
    composerObserver?.disconnect();
    composerObserver = undefined;
    if (composerZoneId !== undefined) {
      const id = composerZoneId;
      composerZoneId = undefined;
      editor.changeViewZones((accessor) => accessor.removeZone(id));
    }
  };

  // Open a new-comment composer below `line` (the toolbar Comment action). Submit posts via onAddComment then
  // closes it; Cancel removes it. clearRender no longer drops it, so a background re-render can't wipe a draft.
  const openNewComposer = (line: number, options: InlineDiffOptions): void => {
    if (options.onAddComment === undefined) {
      return;
    }
    const onAddComment = options.onAddComment;
    closeNewComposer();
    const node = document.createElement("div");
    node.className = "weavie-pr-thread weavie-pr-thread-new";
    // Close on submit: since clearRender no longer drops the composer, the post itself must, else the open zone
    // keeps every review chord declining (composerFocused stays true).
    const composer = buildComposer("Add a comment…", "Comment", (body) => {
      onAddComment(line, body);
      closeNewComposer();
    });
    const cancel = document.createElement("button");
    cancel.type = "button";
    cancel.className = "weavie-pr-composer-cancel";
    cancel.textContent = "Cancel";
    cancel.addEventListener("click", closeNewComposer);
    composer.appendChild(cancel);
    node.appendChild(composer);
    editor.changeViewZones((accessor) => {
      const { id, observer } = addContentSizedZone(accessor, line, node);
      composerZoneId = id;
      composerObserver = observer;
    });
    queueMicrotask(() => node.querySelector("textarea")?.focus());
  };

  // Render each commented line's thread as a view zone below it (a PR file under review). Zones are tracked in zoneIds so the
  // next render clears them.
  const renderPrCommentZones = (
    model: monaco.editor.ITextModel,
    options: InlineDiffOptions,
  ): void => {
    if (options.comments === undefined || options.comments.length === 0) {
      return;
    }
    const byLine = new Map<number, ReviewCommentInfo[]>();
    for (const comment of options.comments) {
      const group = byLine.get(comment.line) ?? [];
      group.push(comment);
      byLine.set(comment.line, group);
    }
    editor.changeViewZones((accessor) => {
      for (const [line, comments] of byLine) {
        const clamped = Math.min(model.getLineCount(), Math.max(1, line));
        const { id, observer } = addContentSizedZone(
          accessor,
          clamped,
          buildCommentThread(comments, options),
        );
        zoneIds.push(id);
        zoneObservers.push(observer);
      }
    });
  };

  // A content widget hugging a hunk's first line, anchored EXACT at the line's end so it sits beside the code.
  const anchoredWidget = (
    id: string,
    model: monaco.editor.ITextModel,
    anchorLine: number,
    dom: HTMLElement,
  ): monaco.editor.IContentWidget => {
    const line = Math.min(model.getLineCount(), Math.max(1, anchorLine));
    return {
      getId: () => `${id}.${line}`,
      getDomNode: () => dom,
      getPosition: () => ({
        position: { lineNumber: line, column: model.getLineMaxColumn(line) },
        preference: [monaco.editor.ContentWidgetPositionPreference.EXACT],
      }),
    };
  };

  // The faded hunk's widget: a "✓ accepted" tag + an inline ↶ undo that un-keeps just that hunk (posts
  // onUnkeepHunk).
  const buildUndoWidget = (
    hunk: AcceptedHunk,
    index: number,
    model: monaco.editor.ITextModel,
    onUnkeep: (hunk: HunkUnkeep) => void,
  ): monaco.editor.IContentWidget => {
    const dom = document.createElement("div");
    dom.className = "weavie-inline-accepted-tag";
    const kept = document.createElement("span");
    kept.className = "weavie-inline-accepted-kept";
    kept.textContent = "✓ accepted";
    const undo = makeButton(
      "weavie-inline-accepted-undo",
      "↶ undo",
      withShortcut("Undo keep", CommandIds.undoKeep),
      () => {
        // Pin the position to this hunk (it's on screen — the user just clicked it) so the restored bright
        // hunk is what the counter names and Keep/Revert act on after the re-render.
        editor.setPosition({ lineNumber: hunk.anchorLine, column: 1 });
        onUnkeep({
          acceptedStart: hunk.acceptedStart,
          acceptedEndExclusive: hunk.acceptedEndExclusive,
          reviewStart: hunk.reviewStart,
          reviewEndExclusive: hunk.reviewEndExclusive,
          acceptedGuardText: hunk.acceptedGuardText,
          guardText: hunk.guardText,
        });
      },
    );
    dom.append(kept, undo);
    return anchoredWidget(`weavie.accepted.${index}`, model, hunk.anchorLine, dom);
  };

  // The pending-band counterpart: ✓ keep / ✕ revert beside a bright hunk's first line — the mouse path to the
  // same per-hunk actions the keyboard chords and toolbar drive.
  const buildPendingWidget = (
    hunk: Hunk,
    index: number,
    model: monaco.editor.ITextModel,
  ): monaco.editor.IContentWidget => {
    const dom = document.createElement("div");
    dom.className = "weavie-inline-pending-tag";
    dom.append(
      makeButton(
        "weavie-inline-pending-keep",
        "✓ keep",
        withShortcut("Keep this change", CommandIds.acceptChange),
        () => keepHunkNow(hunk),
      ),
      makeButton(
        "weavie-inline-pending-revert",
        "✕ revert",
        withShortcut("Revert this change", CommandIds.rejectChange),
        () => revertHunkNow(hunk),
      ),
    );
    return anchoredWidget(`weavie.pending.${index}`, model, hunk.anchorLine, dom);
  };

  const makeButton = (
    className: string,
    label: string,
    title: string,
    onClick: () => void,
  ): HTMLButtonElement => {
    const button = document.createElement("button");
    button.type = "button";
    button.className = className;
    button.textContent = label;
    button.title = title;
    button.addEventListener("click", () => onClick());
    return button;
  };

  // Every toolbar button advertises its shortcut on hover ("<label> (<shortcut>)") using the command's
  // effective keys; unbound commands show just the label.
  const withShortcut = (label: string, commandId: string): string => {
    const keys = findCommand(commandId)?.keys ?? [];
    return keys.length > 0 ? `${label} (${keys.map(formatKey).join(" / ")})` : label;
  };

  // Reveal a hunk's anchor line: center it, land the cursor there, focus the editor.
  const reveal = (line: number): void => {
    editor.revealLineInCenter(line);
    editor.setPosition({ lineNumber: line, column: 1 });
    editor.focus();
  };

  // The line the review position keys on: the cursor while it's in view, else the viewport's vertical
  // center — so after a manual scroll the counter, per-hunk Keep/Revert, and ↑/↓ all track what's on screen.
  const reviewLine = (): number => {
    const cursor = editor.getPosition()?.lineNumber ?? 1;
    const ranges = editor.getVisibleRanges();
    if (
      ranges.length === 0 ||
      ranges.some((r) => r.startLineNumber <= cursor && cursor <= r.endLineNumber)
    ) {
      return cursor;
    }
    return Math.round((ranges[0]!.startLineNumber + ranges[ranges.length - 1]!.endLineNumber) / 2);
  };

  // Jump to the previous/next change hunk (by anchor line), wrapping. Walks all hunks (so a kept one can be
  // revisited), unlike the Keep loop. False when there's no diff to navigate.
  const goToChange = (direction: 1 | -1): boolean => {
    if (currentHunks.length === 0) {
      return false;
    }
    const lines = currentHunks.map((h) => h.anchorLine);
    const current = reviewLine();
    let target: number;
    if (direction === 1) {
      target = lines.find((line) => line > current) ?? lines[0]!;
    } else {
      const before = lines.filter((line) => line < current);
      target = before.length > 0 ? before[before.length - 1]! : lines[lines.length - 1]!;
    }
    reveal(target);
    return true;
  };

  // The hunk at the review line (the last one starting at/before it), defaulting to the first — the subject of
  // a per-hunk Keep / Revert and of the counter's `change j/M`.
  const hunkAtReviewLine = (): Hunk | undefined => {
    if (currentHunks.length === 0) {
      return undefined;
    }
    const line = reviewLine();
    let hunk = currentHunks[0];
    for (const h of currentHunks) {
      if (h.anchorLine <= line) {
        hunk = h;
      } else {
        break;
      }
    }
    return hunk;
  };

  // After a per-hunk keep/revert clears the file's LAST bright hunk, the file lingers in the review set while a
  // faded band remains, so the host's re-emit has acceptedBaseline != current and won't advance — step to the
  // next file ourselves (a no-op for a single-file review). With no faded band the file clears and the
  // controller advances, so callers pass fadedRemains=false there to avoid a double-step.
  const advanceIfExhausted = (kept: Hunk, fadedRemains: boolean): void => {
    if (fadedRemains && !currentHunks.some((h) => h !== kept)) {
      nextFile();
    }
  };

  // A hunk's live-model text plus its keep/revert coordinates — the payload (with concurrency guard) both post.
  const hunkPayload = (model: monaco.editor.ITextModel, hunk: Hunk): HunkRevert => ({
    baselineStart: hunk.baselineStart,
    baselineEndExclusive: hunk.baselineEndExclusive,
    currentStart: hunk.currentStart,
    currentEndExclusive: hunk.currentEndExclusive,
    guardText: model
      .getLinesContent()
      .slice(hunk.currentStart - 1, hunk.currentEndExclusive - 1)
      .join("\n"),
  });

  // Keep one specific hunk: advance the host's review baseline over it (no disk write; same coordinates + guard
  // as a revert) so it drops from the pending diff for good. Keeping doesn't move the live model, so the
  // remaining hunks' anchors hold — reveal the next one now; the host re-emits the diff without the kept hunk.
  // Shared by the cursor chord/toolbar path and the per-hunk inline ✓ keep button.
  const keepHunkNow = (hunk: Hunk): void => {
    const options = currentOptions;
    const model = editor.getModel();
    if (options?.mode !== "applied" || options.onKeepHunk === undefined || model === null) {
      return;
    }
    const remaining = currentHunks.filter((h) => h !== hunk);
    const target = remaining.find((h) => h.anchorLine > hunk.anchorLine) ?? remaining[0];
    options.onKeepHunk(hunkPayload(model, hunk));
    if (target !== undefined) {
      reveal(target.anchorLine);
    }
    advanceIfExhausted(hunk, true); // keeping always leaves the hunk faded, so the re-emit never advances
  };

  // Revert one specific hunk on disk (host splices baseline lines back; web sends coordinates + a guard). The
  // host re-emits the file's diff, re-rendering without the reverted hunk. Shared like keepHunkNow.
  const revertHunkNow = (hunk: Hunk): void => {
    const options = currentOptions;
    const model = editor.getModel();
    if (options?.mode !== "applied" || options.onRevertHunk === undefined || model === null) {
      return;
    }
    // A faded band means kept hunks already exist; reverting the last bright hunk then leaves the file lingering
    // with acceptedBaseline != current, so the re-emit won't advance and we must. Without one the file clears
    // (acceptedBaseline == current) and the controller advances on its own.
    const fadedRemains = hasFadedBand(options);
    options.onRevertHunk(hunkPayload(model, hunk));
    advanceIfExhausted(hunk, fadedRemains);
  };

  // Per-hunk Keep at the cursor; false (the key falls through) outside applied mode.
  const keepHunk = (): boolean => {
    if (currentOptions?.mode !== "applied" || currentOptions.onKeepHunk === undefined) {
      return false;
    }
    const hunk = hunkAtReviewLine();
    if (hunk !== undefined) {
      keepHunkNow(hunk);
    }
    // Fully-kept file at "change 0/0": the toolbar is up, so consume the key — never fall through
    // and let Monaco type into the file under review.
    return true;
  };

  // Per-hunk Revert at the cursor; false outside applied mode.
  const revertHunk = (): boolean => {
    if (currentOptions?.mode !== "applied" || currentOptions.onRevertHunk === undefined) {
      return false;
    }
    const hunk = hunkAtReviewLine();
    if (hunk !== undefined) {
      revertHunkNow(hunk);
    }
    // Same as keepHunk: at "change 0/0" consume the key rather than fall through to the editor.
    return true;
  };

  // Active-diff actions shared by the toolbar buttons and commands; each returns whether it acted, so an
  // unmatched keybinding falls through. accept/reject are per-hunk in applied mode, whole-proposal in review.
  const runAction = (action: (() => void) | undefined): boolean => {
    if (action === undefined) {
      return false;
    }
    action();
    return true;
  };
  // Parked navigator: the review set is non-empty but no changed file is in view, so the toolbar sits at
  // "change 0" without moving the editor. Any nav (or Keep) steps in — opens the first change — at which point
  // the live toolbar takes over. stepIn declines when there's nothing to step into.
  const stepIn = (): boolean => {
    if (parkedReview === undefined) {
      return false;
    }
    parkedReview.stepIn();
    return true;
  };
  // While a new-comment composer is open, the review chords fall through to it: its own keydown handler owns
  // Ctrl+Enter (submit), Ctrl+Backspace (delete word), and arrows (caret). Gate on the zone being open, not
  // document.activeElement — the editor lives in a shadow root, so activeElement is the shadow host, never the
  // composer textarea inside it.
  const composerFocused = (): boolean => composerZoneId !== undefined || focusedComposers > 0;

  const nextChange = (): boolean =>
    composerFocused() ? false : showingParked ? stepIn() : goToChange(1);
  const prevChange = (): boolean =>
    composerFocused() ? false : showingParked ? stepIn() : goToChange(-1);
  const undo = (): boolean => runAction(currentOptions?.onUndo);
  const keepAll = (): boolean => runAction(currentOptions?.onKeepAll);
  // A live review with no file axis (single-file) has no ← / → handler, so the chord would fall through. On
  // Win/Linux that's wanted — ctrl+$mod+←/→ is plain Ctrl+←/→ word-nav. On macOS it's Ctrl+⌘+←/→, which has no
  // native meaning, so falling through just rings the system bell — swallow it instead while a review is up.
  const swallowFileNav = (): boolean => IS_MAC && currentOptions?.mode === "applied";
  const nextFile = (): boolean =>
    composerFocused()
      ? false
      : showingParked
        ? stepIn()
        : runAction(currentOptions?.onNextFile) || swallowFileNav();
  const prevFile = (): boolean =>
    composerFocused()
      ? false
      : showingParked
        ? stepIn()
        : runAction(currentOptions?.onPrevFile) || swallowFileNav();

  // Per-file Keep (applied mode): the host advances the file's whole review baseline to current, dropping it
  // from the review set. Returns false outside applied mode.
  const keepFile = (): boolean =>
    runAction(currentOptions?.mode === "applied" ? currentOptions.onKeepFile : undefined);
  // Per-file Revert (applied mode): the host restores the whole file to its turn baseline on disk; the
  // editor-controller routes this through a confirm before posting. Returns false outside applied mode.
  const revertFile = (): boolean =>
    runAction(currentOptions?.mode === "applied" ? currentOptions.onRevertFile : undefined);

  // Comment on the current cursor line (a PR file under review, which carries onAddComment). Returns false (the
  // key falls through) for a plain turn file or when no diff is active.
  const comment = (): boolean => {
    if (currentOptions?.onAddComment === undefined) {
      return false;
    }
    openNewComposer(editor.getPosition()?.lineNumber ?? 1, currentOptions);
    return true;
  };

  // Keep / Revert act at the toolbar's sticky scope in applied mode (change → hunk, file → whole file, all →
  // the set); in review mode they resolve the openDiff proposal. The plain keys and the toolbar buttons share
  // these, so a keypress always matches the picker.
  const accept = (): boolean => {
    if (composerFocused()) {
      return false; // typing a comment: let the composer's own Ctrl+Enter submit instead of Keeping the diff
    }
    if (showingParked) {
      return stepIn(); // Keep at "change 0" enters the review rather than acting
    }
    const options = currentOptions;
    if (options?.mode !== "applied") {
      return runAction(options?.onAccept);
    }
    const scope = currentScope;
    return scope === "change" ? keepHunk() : scope === "file" ? keepFile() : keepAll();
  };
  const reject = (): boolean => {
    if (composerFocused()) {
      return false; // typing a comment: let Ctrl+Backspace delete a word instead of Reverting the diff
    }
    if (showingParked) {
      return false; // nothing to revert from "change 0"
    }
    const options = currentOptions;
    if (options?.mode !== "applied") {
      return runAction(options?.onReject);
    }
    const scope = currentScope;
    return scope === "change" ? revertHunk() : scope === "file" ? revertFile() : undo();
  };

  // Review undo/redo (session-global; bound once via bindHistory). While a review surface is up the undo chords
  // CONSUME the key even with nothing to undo — never fall through and let Monaco insert a newline (Shift+Enter)
  // into the file under review. They only decline (fall through to the editor) when no review is up at all.
  // A review surface is up (a live diff or the parked navigator), or there's undo history to act on — in either
  // case the undo chords are meaningful and must consume the key rather than type into the editor.
  const reviewUp = (): boolean =>
    parkedReview !== undefined || currentOptions?.mode === "applied" || history.canUndo;
  const undoKeep = (): boolean => {
    if (!reviewUp()) {
      return false;
    }
    if (history.canUndoKeep) {
      runAction(historyHandlers?.onUndoKeep);
    }
    return true;
  };
  const undoRevert = (): boolean => {
    if (!reviewUp()) {
      return false;
    }
    if (history.canUndoRevert) {
      runAction(historyHandlers?.onUndoRevert);
    }
    return true;
  };
  const undoLast = (): boolean =>
    history.canUndo ? runAction(historyHandlers?.onUndoLast) : false;
  const redoReview = (): boolean => (history.canRedo ? runAction(historyHandlers?.onRedo) : false);

  // Dim/enable the toolbar's Undo/Redo buttons to match availability (cheap — no full re-render).
  const syncHistoryButtons = (): void => {
    if (undoButton !== undefined) {
      undoButton.disabled = !history.canUndo;
    }
    if (redoButton !== undefined) {
      redoButton.disabled = !history.canRedo;
    }
  };

  // Set the sticky scope the Keep / Revert buttons act on and re-render so their labels/handlers follow.
  const setScope = (scope: ReviewScope): void => {
    if (currentOptions?.mode !== "applied") {
      return;
    }
    currentScope = scope;
    renderActive();
  };

  const scopeName = (scope: ReviewScope): string =>
    scope === "change" ? "Change" : scope === "file" ? "File" : "All";

  // Repaint the applied toolbar's `file i/N · change j/M` subtitle + change dots for the hunk at the review
  // line. A cheap DOM-only update fired on cursor move and scroll (no full re-render); no-op outside applied mode.
  const renderCounter = (): void => {
    const options = currentOptions;
    if (counterNode === undefined || options === undefined) {
      return;
    }
    const total = currentHunks.length;
    const hunk = hunkAtReviewLine();
    const idx = hunk === undefined ? -1 : currentHunks.indexOf(hunk);
    const labelPart = options.reviewLabel === undefined ? "" : `${options.reviewLabel} · `;
    const filePart =
      options.fileCount !== undefined && options.fileCount > 1 && options.fileIndex !== undefined
        ? `file ${options.fileIndex}/${options.fileCount} · `
        : "";
    counterNode.textContent = `${labelPart}${filePart}change ${idx < 0 ? 0 : idx + 1}/${total}`;
    if (dotsNode === undefined) {
      return;
    }
    dotsNode.replaceChildren();
    if (total > 1 && total <= MAX_CHANGE_DOTS) {
      for (let i = 0; i < total; i++) {
        const dot = document.createElement("i");
        dot.className = i === idx ? "on" : i < idx ? "done" : "";
        dotsNode.appendChild(dot);
      }
    }
  };

  const navButtons = (): HTMLElement[] => [
    makeButton(
      "weavie-inline-nav",
      "↑",
      withShortcut("Previous change", CommandIds.prevChange),
      prevChange,
    ),
    makeButton(
      "weavie-inline-nav",
      "↓",
      withShortcut("Next change", CommandIds.nextChange),
      nextChange,
    ),
  ];

  // The scope dropdown: a `Scope: <X> ▾` toggle over an "Apply to…" menu whose items set the sticky scope the
  // Keep / Revert buttons act on, with per-item counts naming each scope's reach.
  const buildScopePicker = (options: InlineDiffOptions): HTMLElement => {
    const scope = currentScope;
    const wrap = document.createElement("div");
    wrap.className = "weavie-inline-scope";
    scopeWrapNode = wrap;
    const menu = document.createElement("div");
    menu.className = "weavie-inline-scope-menu";
    menu.style.display = "none";
    scopeMenuNode = menu;
    const toggle = makeButton(
      "weavie-inline-scope-btn",
      `Scope: ${scopeName(scope)} ▾`,
      "Choose what Keep / Revert act on",
      () => {
        menu.style.display = menu.style.display === "none" ? "flex" : "none";
      },
    );
    const head = document.createElement("div");
    head.className = "weavie-inline-scope-head";
    head.textContent = "Apply to…";
    menu.appendChild(head);
    const addItem = (value: ReviewScope, label: string, count: number | undefined): void => {
      const item = makeButton(
        `weavie-inline-scope-item${value === scope ? " active" : ""}`,
        label,
        label,
        () => setScope(value),
      );
      if (count !== undefined) {
        const tag = document.createElement("span");
        tag.className = "weavie-inline-scope-count";
        tag.textContent = String(count);
        item.appendChild(tag);
      }
      menu.appendChild(item);
    };
    addItem("change", "This change", undefined);
    addItem("file", "This file", currentHunks.length);
    // "All" always offered — it's the only Keep/Revert scope that commits the whole review and closes the
    // navigator (keep-all / revert-all), so a single-file review still has a way out. "All files" reads wrong
    // for one file, so name it "All changes" (counting hunks) there.
    const manyFiles = (options.fileCount ?? 1) > 1;
    addItem(
      "all",
      manyFiles ? "All files" : "All changes",
      manyFiles ? options.fileCount : currentHunks.length,
    );
    wrap.append(toggle, menu);
    return wrap;
  };

  // The applied-review toolbar: a 2D navigator (files ← →, hunks ↑ ↓) around a stacked filename + counter,
  // then a scope picker feeding Keep / Revert buttons whose label/handler/shortcut follow the sticky scope.
  const buildAppliedBar = (bar: HTMLElement, options: InlineDiffOptions): void => {
    const multiFile =
      options.fileCount !== undefined &&
      options.fileCount > 1 &&
      options.onPrevFile !== undefined &&
      options.onNextFile !== undefined;
    if (multiFile) {
      bar.appendChild(
        makeButton(
          "weavie-inline-file",
          "←",
          withShortcut("Previous file", CommandIds.reviewPrevFile),
          prevFile,
        ),
      );
    }
    const stack = document.createElement("div");
    stack.className = "weavie-inline-stack";
    const name = document.createElement("span");
    name.className = "weavie-inline-stack-name";
    name.textContent = options.fileLabel ?? "";
    counterNode = document.createElement("span");
    counterNode.className = "weavie-inline-stack-sub";
    stack.append(name, counterNode);
    bar.appendChild(stack);
    if (multiFile) {
      bar.appendChild(
        makeButton(
          "weavie-inline-file",
          "→",
          withShortcut("Next file", CommandIds.reviewNextFile),
          nextFile,
        ),
      );
    }
    dotsNode = document.createElement("span");
    dotsNode.className = "weavie-inline-dots";
    bar.appendChild(dotsNode);
    bar.append(...navButtons());
    const divider = document.createElement("span");
    divider.className = "weavie-inline-divider";
    bar.appendChild(divider);
    bar.appendChild(buildScopePicker(options));

    // Keep / Revert always carry the plain chords (Ctrl+Enter / Ctrl+Backspace); accept/reject route to the
    // sticky scope, so the buttons and the keys stay in lockstep. Only the tooltip names the current scope.
    const scope = currentScope;
    const allTarget = (options.fileCount ?? 1) > 1 ? "all files" : "all changes";
    const keepTip =
      scope === "change"
        ? "Keep this change"
        : scope === "file"
          ? "Keep this file"
          : `Keep ${allTarget}`;
    const revertTip =
      scope === "change"
        ? "Revert this change"
        : scope === "file"
          ? "Revert this file"
          : `Revert ${allTarget}`;
    bar.appendChild(
      makeButton(
        "weavie-inline-accept",
        "Keep",
        withShortcut(keepTip, CommandIds.acceptChange),
        accept,
      ),
    );
    bar.appendChild(
      makeButton(
        "weavie-inline-reject",
        "Revert",
        withShortcut(revertTip, CommandIds.rejectChange),
        reject,
      ),
    );
    // A PR file also carries review comments, so Comment/Reply sit beside Keep/Revert on the one toolbar (a plain
    // turn file has no onAddComment, so no button).
    if (options.onAddComment !== undefined) {
      bar.appendChild(
        makeButton(
          "weavie-inline-comment",
          "Comment",
          withShortcut("Add a comment on the current line", CommandIds.reviewComment),
          () => openNewComposer(editor.getPosition()?.lineNumber ?? 1, options),
        ),
      );
    }
    // Undo / Redo of review actions (session-global). The generic Undo reverses the most recent of either kind;
    // its tooltip names the two type-split chords. Both dim when there's nothing to do (syncHistoryButtons).
    const histDivider = document.createElement("span");
    histDivider.className = "weavie-inline-divider";
    bar.appendChild(histDivider);
    undoButton = makeButton(
      "weavie-inline-hist",
      "↶",
      `Undo last review action — ${withShortcut("keep", CommandIds.undoKeep)}, ${withShortcut("revert", CommandIds.undoRevert)}`,
      undoLast,
    );
    redoButton = makeButton(
      "weavie-inline-hist",
      "↷",
      withShortcut("Redo review action", CommandIds.redoReview),
      redoReview,
    );
    bar.append(undoButton, redoButton);
    syncHistoryButtons();
    renderCounter();
  };

  // The floating action bar. Applied mode is the 2D scope navigator (buildAppliedBar), which also carries
  // Comment/Reply for a PR file; review mode is the hunk arrows + Keep/Reject for the proposal; view mode is
  // arrows alone.
  const buildToolbar = (options: InlineDiffOptions): HTMLElement => {
    const bar = document.createElement("div");
    bar.className = "weavie-inline-toolbar";
    if (options.mode === "applied") {
      buildAppliedBar(bar, options);
      return bar;
    }
    bar.append(...navButtons());
    if (options.mode === "review") {
      if (options.onAccept !== undefined) {
        bar.appendChild(
          makeButton(
            "weavie-inline-accept",
            "Keep",
            withShortcut("Keep this change", CommandIds.acceptChange),
            accept,
          ),
        );
      }
      if (options.onReject !== undefined) {
        bar.appendChild(
          makeButton(
            "weavie-inline-reject",
            "Reject",
            withShortcut("Reject this change", CommandIds.rejectChange),
            reject,
          ),
        );
      }
    }
    return bar;
  };

  const render = (uriString: string): void => {
    clearRender();
    const model = editor.getModel();
    if (model === null || model.uri.toString() !== uriString) {
      return;
    }
    const options = diffs.get(uriString);
    if (options === undefined) {
      return;
    }

    const original = splitLines(options.original);
    const modified = model.getLinesContent();
    const { changes } = linesDiffComputers
      .getDefault()
      .computeDiff(original, modified, DIFF_OPTIONS);
    // A fully-kept file has no bright (pending) hunks but still carries a faded accepted band — don't bail on it.
    if (changes.length === 0 && !hasFadedBand(options)) {
      return; // no net change and nothing kept — nothing to render
    }

    // A wholly-new file (empty baseline) has every line "added"; stacking the per-line wash + char overlay across
    // all of them slabs the editor in green. Mark it with one continuous gutter edge + a "New file" header instead.
    const isNewFile = options.original.length === 0;

    // Lines the user typed (diff the live model against `claudeVersion`) render fainter. Empty when
    // claudeVersion is omitted or the model still matches it.
    const userLines = new Set<number>();
    if (options.claudeVersion !== undefined) {
      const userDiff = linesDiffComputers
        .getDefault()
        .computeDiff(splitLines(options.claudeVersion), modified, DIFF_OPTIONS);
      for (const change of userDiff.changes) {
        for (
          let ln = change.modified.startLineNumber;
          ln < change.modified.endLineNumberExclusive;
          ln++
        ) {
          userLines.add(ln);
        }
      }
    }

    const deltas: monaco.editor.IModelDeltaDecoration[] = [];
    const ghosts: { afterLineNumber: number; lines: string[]; faded?: boolean }[] = [];
    const hunks: Hunk[] = [];
    const acceptedHunks: AcceptedHunk[] = [];

    for (const change of changes) {
      hunks.push({
        anchorLine: Math.max(1, change.modified.startLineNumber),
        baselineStart: change.original.startLineNumber,
        baselineEndExclusive: change.original.endLineNumberExclusive,
        currentStart: change.modified.startLineNumber,
        currentEndExclusive: change.modified.endLineNumberExclusive,
      });
      if (!change.modified.isEmpty) {
        // Per-line so a block mixing Claude's lines with the user's tweaks paints each in its own shade. A new file
        // skips the wash + char overlay (isNewFile) — only the continuous gutter edge marks it.
        for (
          let ln = change.modified.startLineNumber;
          ln < change.modified.endLineNumberExclusive;
          ln++
        ) {
          const fromUser = userLines.has(ln);
          deltas.push({
            range: new monaco.Range(ln, 1, ln, 1),
            options: {
              isWholeLine: true,
              className: isNewFile ? null : fromUser ? "weavie-inline-user" : "weavie-inline-added",
              linesDecorationsClassName: isNewFile
                ? "weavie-inline-added-gutter"
                : fromUser
                  ? "weavie-inline-user-gutter"
                  : "weavie-inline-added-gutter",
              overviewRuler: {
                // Standard VS Code added-marker id so the ruler tracks the theme; the added/user shade
                // distinction is carried by the in-editor line wash, not the ruler.
                color: { id: "editorOverviewRuler.addedForeground" },
                position: monaco.editor.OverviewRulerLane.Left,
              },
            },
          });
        }
        if (!isNewFile) {
          for (const inner of change.innerChanges ?? []) {
            const r = inner.modifiedRange;
            // Char-level emphasis is for Claude's edits; skip it on the user's own faint lines.
            if (userLines.has(r.startLineNumber)) {
              continue;
            }
            const empty = r.startLineNumber === r.endLineNumber && r.startColumn === r.endColumn;
            if (!empty) {
              // className (not inlineClassName): an overlay div spanning the full line height, like VS Code's
              // char-insert — an inline span's background stops short of it, leaving a seam between lines.
              deltas.push({
                range: r,
                options: { className: "weavie-inline-added-text", shouldFillLineOnLineBreak: true },
              });
            }
          }
        }
      }
      // A new file's "removed" side is only the empty baseline line — no ghost worth showing.
      if (!isNewFile && !change.original.isEmpty) {
        ghosts.push({
          afterLineNumber: Math.max(0, change.modified.startLineNumber - 1),
          lines: original.slice(
            change.original.startLineNumber - 1,
            change.original.endLineNumberExclusive - 1,
          ),
        });
      }
    }

    // The faded "accepted" band: kept-but-uncommitted hunks (acceptedBaseline → review baseline). They're EQUAL
    // between the review baseline (`original`) and the live model — a keep made them so — so they sit in the
    // UNCHANGED regions of the bright diff above. Translate each one's review-baseline position into a live model
    // line via that diff, wash it faded green in place, and hang an inline ↶ undo beside it. The faded band is a
    // pure overlay: it never enters `hunks`, so ↑/↓ and Keep/Revert only ever touch the bright pending hunks.
    if (hasFadedBand(options) && options.acceptedBaseline !== undefined) {
      const accepted = splitLines(options.acceptedBaseline);
      const fadedChanges = linesDiffComputers
        .getDefault()
        .computeDiff(accepted, original, DIFF_OPTIONS).changes;
      for (const change of fadedChanges) {
        const reviewStart = change.modified.startLineNumber;
        const reviewEndExclusive = change.modified.endLineNumberExclusive;
        const modelStart = reviewToModelLine(changes, reviewStart);
        for (let i = 0; i < reviewEndExclusive - reviewStart; i++) {
          const ln = modelStart + i;
          deltas.push({
            range: new monaco.Range(ln, 1, ln, 1),
            options: {
              isWholeLine: true,
              className: "weavie-inline-accepted",
              linesDecorationsClassName: "weavie-inline-accepted-gutter",
              overviewRuler: {
                color: { id: "editorOverviewRuler.addedForeground" },
                position: monaco.editor.OverviewRulerLane.Left,
              },
            },
          });
        }
        if (!change.original.isEmpty) {
          ghosts.push({
            afterLineNumber: Math.max(0, modelStart - 1),
            lines: accepted.slice(
              change.original.startLineNumber - 1,
              change.original.endLineNumberExclusive - 1,
            ),
            faded: true,
          });
        }
        acceptedHunks.push({
          anchorLine: Math.max(1, modelStart),
          acceptedStart: change.original.startLineNumber,
          acceptedEndExclusive: change.original.endLineNumberExclusive,
          reviewStart,
          reviewEndExclusive,
          acceptedGuardText: accepted
            .slice(change.original.startLineNumber - 1, change.original.endLineNumberExclusive - 1)
            .join("\n"),
          guardText: original.slice(reviewStart - 1, reviewEndExclusive - 1).join("\n"),
        });
      }
    }

    decorations = editor.createDecorationsCollection(deltas);
    editor.changeViewZones((accessor) => {
      if (isNewFile) {
        zoneIds.push(
          accessor.addZone({
            afterLineNumber: 0,
            heightInPx: NEW_FILE_BADGE_HEIGHT,
            domNode: buildNewFileBadge(),
          }),
        );
      }
      for (const ghost of ghosts) {
        zoneIds.push(
          accessor.addZone({
            afterLineNumber: ghost.afterLineNumber,
            heightInLines: ghost.lines.length,
            domNode: buildGhost(ghost.lines, ghost.faded === true),
          }),
        );
      }
    });

    // Comment threads (a PR file under applied review); no-op for a plain turn file (no comments).
    if (options.comments !== undefined) {
      renderPrCommentZones(model, options);
    }

    currentOptions = options;
    currentHunks = hunks;
    showingParked = false;
    const editorDom = editor.getDomNode();
    if (editorDom !== null) {
      toolbarNode = buildToolbar(options);
      editorDom.appendChild(toolbarNode);
    }
    // The inline ✓ keep / ✕ revert widgets on each bright pending hunk (applied review only).
    if (
      options.mode === "applied" &&
      options.onKeepHunk !== undefined &&
      options.onRevertHunk !== undefined
    ) {
      hunks.forEach((hunk, index) => {
        const widget = buildPendingWidget(hunk, index, model);
        hunkWidgets.push(widget);
        editor.addContentWidget(widget);
      });
    }
    // The inline ↶ undo widgets (faded band only); no-op when there's no accepted band or no un-keep handler.
    if (options.onUnkeepHunk !== undefined) {
      const onUnkeep = options.onUnkeepHunk;
      acceptedHunks.forEach((hunk, index) => {
        const widget = buildUndoWidget(hunk, index, model, onUnkeep);
        hunkWidgets.push(widget);
        editor.addContentWidget(widget);
      });
    }
    renderedUri = uriString;
  };

  // The parked toolbar: the same bottom-center bar as a live review, sitting at "change 0" over whatever the
  // editor shows. Its nav + Keep step into the review (stepIn); Keep/Revert are inert until then; Undo/Redo
  // still reflect the session history. Reuses the live toolbar's classes so stepping in is a seamless expand.
  const renderParked = (): void => {
    clearRender();
    const editorDom = editor.getDomNode();
    if (editorDom === null || parkedReview === undefined) {
      return;
    }
    const bar = document.createElement("div");
    bar.className = "weavie-inline-toolbar";
    const multiFile = parkedReview.fileCount > 1;
    if (multiFile) {
      bar.appendChild(
        makeButton(
          "weavie-inline-file",
          "←",
          withShortcut("Review changes", CommandIds.reviewPrevFile),
          stepIn,
        ),
      );
    }
    const stack = document.createElement("div");
    stack.className = "weavie-inline-stack";
    const name = document.createElement("span");
    name.className = "weavie-inline-stack-name";
    name.textContent = "Review changes";
    const sub = document.createElement("span");
    sub.className = "weavie-inline-stack-sub";
    const parkedLabel = parkedReview.label === undefined ? "" : `${parkedReview.label} · `;
    sub.textContent = `${parkedLabel}${parkedReview.fileCount} file${parkedReview.fileCount === 1 ? "" : "s"} · press ↓ to start`;
    stack.append(name, sub);
    bar.appendChild(stack);
    if (multiFile) {
      bar.appendChild(
        makeButton(
          "weavie-inline-file",
          "→",
          withShortcut("Review changes", CommandIds.reviewNextFile),
          stepIn,
        ),
      );
    }
    bar.append(
      makeButton(
        "weavie-inline-nav",
        "↑",
        withShortcut("Review changes", CommandIds.prevChange),
        stepIn,
      ),
      makeButton(
        "weavie-inline-nav",
        "↓",
        withShortcut("Review changes", CommandIds.nextChange),
        stepIn,
      ),
    );
    const divider = document.createElement("span");
    divider.className = "weavie-inline-divider";
    bar.appendChild(divider);
    // Inert until a change is in view, but shown so the bar reads as the same toolbar at "change 0".
    const keep = makeButton(
      "weavie-inline-accept",
      "Keep",
      "Step into a change first (↓)",
      () => {},
    );
    const revert = makeButton(
      "weavie-inline-reject",
      "Revert",
      "Step into a change first (↓)",
      () => {},
    );
    keep.disabled = true;
    revert.disabled = true;
    bar.append(keep, revert);
    const histDivider = document.createElement("span");
    histDivider.className = "weavie-inline-divider";
    bar.appendChild(histDivider);
    undoButton = makeButton(
      "weavie-inline-hist",
      "↶",
      `Undo last review action — ${withShortcut("keep", CommandIds.undoKeep)}, ${withShortcut("revert", CommandIds.undoRevert)}`,
      undoLast,
    );
    redoButton = makeButton(
      "weavie-inline-hist",
      "↷",
      withShortcut("Redo review action", CommandIds.redoReview),
      redoReview,
    );
    bar.append(undoButton, redoButton);
    syncHistoryButtons();
    toolbarNode = bar;
    editorDom.appendChild(bar);
    showingParked = true;
  };

  // True when the diff commands (Next/Previous/Keep/Revert Change, Undo All) would actually act: a diff is shown
  // for the active model, or a review set is pending (their chords step into it). Gates them out of the palette
  // otherwise — an empty workspace shouldn't lead with commands that silently no-op (#137).
  const syncDiffContext = (): void => {
    const model = editor.getModel();
    const active =
      (model !== null && diffs.has(model.uri.toString())) ||
      (parkedReview !== undefined && parkedReview.fileCount > 0);
    setContext("diffActive", active);
  };

  // Render the active model's diff if it has one; else park the navigator when a review set is pending; else clear.
  const renderActive = (): void => {
    syncDiffContext();
    const model = editor.getModel();
    if (model !== null && diffs.has(model.uri.toString())) {
      render(model.uri.toString());
    } else if (parkedReview !== undefined && parkedReview.fileCount > 0) {
      renderParked();
    } else {
      clearRender();
    }
  };

  const scheduleRender = (): void => {
    if (recomputeTimer !== undefined) {
      clearTimeout(recomputeTimer);
    }
    recomputeTimer = setTimeout(() => {
      recomputeTimer = undefined;
      renderActive();
    }, RECOMPUTE_DEBOUNCE_MS);
  };

  // View zones are lost on model swap — close any open composer (its zone is gone) and re-render the new model.
  const onModel = editor.onDidChangeModel(() => {
    closeNewComposer();
    renderActive();
  });
  const onContent = editor.onDidChangeModelContent(scheduleRender);
  const offFonts = onFontsChanged(renderActive);
  // Live-update the applied toolbar's change counter + dots as the cursor walks hunks (no full re-render).
  const onCursor = editor.onDidChangeCursorPosition(renderCounter);
  // Manual scrolling moves the review position too (reviewLine follows the viewport once the cursor leaves
  // it), so the counter tracks scroll as well as cursor moves.
  const onScroll = editor.onDidScrollChange(renderCounter);
  // Close an open scope dropdown on any click outside it (capture so it beats the editor's own handlers).
  const onDocDown = (event: PointerEvent): void => {
    if (
      scopeMenuNode !== undefined &&
      scopeMenuNode.style.display !== "none" &&
      scopeWrapNode !== undefined &&
      !scopeWrapNode.contains(event.target as Node)
    ) {
      scopeMenuNode.style.display = "none";
    }
  };
  document.addEventListener("pointerdown", onDocDown, true);
  // Escape closes an open scope dropdown from the keyboard (capture so it beats the editor's own Escape).
  const onDocKey = (event: KeyboardEvent): void => {
    if (
      event.key === "Escape" &&
      scopeMenuNode !== undefined &&
      scopeMenuNode.style.display !== "none"
    ) {
      event.stopPropagation();
      scopeMenuNode.style.display = "none";
    }
  };
  document.addEventListener("keydown", onDocKey, true);

  // Register/remove a diff keyed by an exact model URI string (the path-based set/clear convert a file path
  // to its file:// URI; the review path passes the transient model's URI).
  const setByUri = (key: string, options: InlineDiffOptions): void => {
    diffs.set(key, options);
    syncDiffContext();
    const model = editor.getModel();
    if (model !== null && model.uri.toString() === key) {
      render(key);
    }
  };
  const clearByUri = (key: string): void => {
    diffs.delete(key);
    syncDiffContext(); // an off-screen clear still changes whether any diff is active
    if (renderedUri === key) {
      renderActive(); // fall back to the parked navigator when a review set still remains
    }
  };

  return {
    set(path, options) {
      setByUri(monaco.Uri.file(canonicalFsPath(path)).toString(), options);
    },
    clear(path) {
      clearByUri(monaco.Uri.file(canonicalFsPath(path)).toString());
    },
    setByUri,
    clearByUri,
    clearAll() {
      diffs.clear();
      currentScope = "change";
      parkedReview = undefined;
      closeNewComposer();
      clearRender();
      syncDiffContext();
    },
    hasDiffForUri: (uri) => diffs.has(uri),
    nextChange,
    prevChange,
    nextFile,
    prevFile,
    accept,
    reject,
    undo,
    keepFile,
    revertFile,
    keepAll,
    comment,
    undoKeep,
    undoRevert,
    redoReview,
    bindHistory(handlers) {
      historyHandlers = handlers;
    },
    setReviewHistory(state) {
      history = state;
      syncHistoryButtons();
    },
    setParkedReview(summary) {
      parkedReview = summary;
      renderActive();
    },
    dispose() {
      if (recomputeTimer !== undefined) {
        clearTimeout(recomputeTimer);
      }
      document.removeEventListener("pointerdown", onDocDown, true);
      document.removeEventListener("keydown", onDocKey, true);
      onCursor.dispose();
      onScroll.dispose();
      onModel.dispose();
      onContent.dispose();
      offFonts();
      closeNewComposer();
      clearRender();
    },
  };
}
