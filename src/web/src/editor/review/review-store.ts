import { type Accessor, createSignal } from "solid-js";
import type { ClientSession, ReviewCommentInfo } from "../../bridge";
import { setContext } from "../../commands/context";
import { normalizePath, samePath } from "../fs-path";

/** One changed file in a review, including aggregate counts and its first changed line. */
export interface ReviewFile {
  path: string;
  name: string;
  added: number;
  removed: number;
  line: number;
  currentExists: boolean;
}

/** The three authoritative text boundaries needed by both review presentations. */
export interface ReviewFileDiff {
  path: string;
  name: string;
  acceptedBaseline: string;
  acceptedBaselineExists: boolean;
  baseline: string;
  baselineExists: boolean;
  current: string;
  currentExists: boolean;
}

export interface ReviewComments {
  number: number;
  path: string;
  comments: ReviewCommentInfo[];
}

export interface ReviewHistory {
  canUndo: boolean;
  canUndoKeep: boolean;
  canUndoRevert: boolean;
  canRedo: boolean;
}

export const EMPTY_REVIEW_HISTORY: ReviewHistory = {
  canUndo: false,
  canUndoKeep: false,
  canUndoRevert: false,
  canRedo: false,
};

export type ReviewPresentationMode = "file" | "unified";

/** One step of the review walk. Its surface — unified overview or file review — owns what a step means. */
export type ReviewStep = "nextChange" | "prevChange" | "nextFile" | "prevFile";

/** The mounted unified surface's own walk, so a review chord moves the overview instead of opening a file. */
export type UnifiedReviewNavigator = Record<ReviewStep, () => boolean>;

export interface ReviewCursor {
  path: string;
  line: number;
}

/** Stable per-file projection. A diff/comment push updates only its matching accessors. */
export interface ReviewFileView {
  summary: Accessor<ReviewFile>;
  diff: Accessor<ReviewFileDiff | null>;
  comments: Accessor<ReviewComments | null>;
  collapsed: Accessor<boolean>;
  /** Whether the host has pushed this file's diff yet — a fully reviewed file has no diff but is loaded. */
  loaded: Accessor<boolean>;
  /** Whether anything in this file still needs review. The authoritative answer; never re-derive it. */
  pending: Accessor<boolean>;
}

export interface ReviewOverview {
  label: string;
  files: ReviewFileView[];
  added: number;
  removed: number;
  cursor: ReviewCursor | null;
  fullyLoaded: Accessor<boolean>;
  hasPending: Accessor<boolean>;
}

interface ReviewEntry {
  summary: ReviewFile | null;
  diff: ReviewFileDiff | null;
  comments: ReviewComments | null;
  pending: boolean | null;
  collapsed: boolean;
  /** This file's last pushed state, so "reviewed" can be pinned to the exact thing the user reviewed. */
  signature: string;
  /** The signature the file was marked reviewed at; null while it still needs review. */
  reviewedAt: string | null;
  view: ReviewFileView | null;
  touch: (() => void) | null;
}

/** Identifies exactly what a file presents for review: its content, its existence, and whether it's pending. */
function reviewSignature(diff: ReviewFileDiff, pending: boolean): string {
  return `${pending ? "1" : "0"}\0${diff.currentExists ? "1" : "0"}\0${diff.current}`;
}

export interface SessionReviewBoard {
  files: ReviewFileView[];
  label: string;
  added: number;
  removed: number;
  history: ReviewHistory;
  mode: ReviewPresentationMode;
  cursor: ReviewCursor | null;
}

interface MutableReviewBoard extends SessionReviewBoard {
  entries: Map<string, ReviewEntry>;
}

export interface ReviewStore {
  mode: Accessor<ReviewPresentationMode>;
  overview: Accessor<ReviewOverview>;
  count: Accessor<number>;
  board(session: ClientSession): SessionReviewBoard;
  select(session: ClientSession | null): void;
  setFiles(session: ClientSession, files: ReviewFile[], label: string): SessionReviewBoard;
  setDiff(session: ClientSession, diff: ReviewFileDiff): SessionReviewBoard;
  setComments(session: ClientSession, comments: ReviewComments): SessionReviewBoard;
  setHistory(session: ClientSession, history: ReviewHistory): SessionReviewBoard;
  setFileCollapsed(session: ClientSession, path: string, collapsed: boolean): SessionReviewBoard;
  reset(session: ClientSession): SessionReviewBoard;
  enterUnified(session: ClientSession, cursor: ReviewCursor | null): string[];
  enterFile(session: ClientSession, cursor: ReviewCursor): void;
  setCursor(session: ClientSession, cursor: ReviewCursor): void;
  leaveUnified(session: ClientSession): void;
}

const emptyOverview = (): ReviewOverview => ({
  label: "",
  files: [],
  added: 0,
  removed: 0,
  cursor: null,
  fullyLoaded: () => true,
  hasPending: () => false,
});

/** Owns the authoritative web mirror of each session's review board and its selected projection. */
export function createReviewStore(): ReviewStore {
  const boards = new WeakMap<ClientSession, MutableReviewBoard>();
  const [mode, setMode] = createSignal<ReviewPresentationMode>("file");
  const [overview, setOverview] = createSignal<ReviewOverview>(emptyOverview());
  const [count, setCount] = createSignal(0);
  let selected: ClientSession | null = null;

  const board = (session: ClientSession): MutableReviewBoard => {
    const existing = boards.get(session);
    if (existing !== undefined) {
      return existing;
    }
    const created: MutableReviewBoard = {
      entries: new Map(),
      files: [],
      label: "",
      added: 0,
      removed: 0,
      history: EMPTY_REVIEW_HISTORY,
      mode: "file",
      cursor: null,
    };
    boards.set(session, created);
    return created;
  };

  const publish = (session: ClientSession, state: MutableReviewBoard): void => {
    if (selected !== session) {
      return;
    }
    setMode(state.mode);
    setCount(state.files.length);
    setOverview({
      label: state.label,
      files: state.files,
      added: state.added,
      removed: state.removed,
      cursor: state.cursor,
      fullyLoaded: () => state.files.every((file) => file.loaded()),
      hasPending: () => state.files.some((file) => file.pending()),
    });
    setContext("reviewSetActive", state.files.length > 0);
    setContext("unifiedReviewActive", state.mode === "unified" && state.files.length > 0);
  };

  const ensureEntry = (state: MutableReviewBoard, path: string): ReviewEntry => {
    const key = normalizePath(path);
    const existing = state.entries.get(key);
    if (existing !== undefined) {
      return existing;
    }
    const created: ReviewEntry = {
      summary: null,
      diff: null,
      comments: null,
      pending: null,
      collapsed: false,
      signature: "",
      reviewedAt: null,
      view: null,
      touch: null,
    };
    state.entries.set(key, created);
    return created;
  };

  const attachView = (entry: ReviewEntry, summary: ReviewFile): ReviewFileView => {
    entry.summary = summary;
    if (entry.view !== null) {
      entry.touch?.();
      return entry.view;
    }
    const [revision, setRevision] = createSignal(0);
    entry.touch = () => setRevision((value) => value + 1);
    entry.view = {
      summary: () => {
        revision();
        return entry.summary!;
      },
      diff: () => {
        revision();
        return entry.diff;
      },
      comments: () => {
        revision();
        return entry.comments;
      },
      collapsed: () => {
        revision();
        return entry.collapsed;
      },
      loaded: () => {
        revision();
        return entry.pending !== null;
      },
      pending: () => {
        revision();
        return entry.pending === true;
      },
    };
    return entry.view;
  };

  const repairCursor = (state: MutableReviewBoard): void => {
    const first = state.files[0]?.summary();
    if (first === undefined) {
      state.mode = "file";
      state.cursor = null;
    } else if (
      state.cursor !== null &&
      !state.files.some((file) => samePath(file.summary().path, state.cursor?.path ?? ""))
    ) {
      state.cursor = { path: first.path, line: first.line };
    }
  };

  const setFiles = (
    session: ClientSession,
    files: ReviewFile[],
    label: string,
  ): SessionReviewBoard => {
    const state = board(session);
    const live = new Set(files.map((file) => normalizePath(file.path)));
    for (const key of state.entries.keys()) {
      if (!live.has(key)) {
        state.entries.delete(key);
      }
    }
    state.files = files.map((file) => attachView(ensureEntry(state, file.path), file));
    state.label = label;
    state.added = files.reduce((total, file) => total + file.added, 0);
    state.removed = files.reduce((total, file) => total + file.removed, 0);
    repairCursor(state);
    publish(session, state);
    return state;
  };

  const setDiff = (session: ClientSession, diff: ReviewFileDiff): SessionReviewBoard => {
    const state = board(session);
    const entry = ensureEntry(state, diff.path);
    const pending = diff.baseline !== diff.current || diff.baselineExists !== diff.currentExists;
    entry.diff =
      diff.acceptedBaseline === diff.current && diff.acceptedBaselineExists === diff.currentExists
        ? null
        : diff;
    // "Reviewed" is a claim about one exact state: anything new in the file un-reviews it, because there is now
    // something the user hasn't seen. A file with nothing left pending is reviewed at whatever it now holds.
    const signature = reviewSignature(diff, pending);
    const moved = entry.signature !== signature;
    if (entry.reviewedAt !== null && entry.reviewedAt !== signature) {
      entry.collapsed = false;
      entry.reviewedAt = null;
    }
    entry.signature = signature;
    // Only the transition into "nothing left pending" folds a file away; a redundant re-push of the same state
    // must not undo a fold the user deliberately opened.
    if (!pending && moved) {
      entry.collapsed = true;
      entry.reviewedAt = signature;
    }
    entry.pending = pending;
    entry.touch?.();
    return state;
  };

  const setComments = (session: ClientSession, comments: ReviewComments): SessionReviewBoard => {
    const state = board(session);
    const entry = ensureEntry(state, comments.path);
    entry.comments = comments;
    entry.touch?.();
    return state;
  };

  const setHistory = (session: ClientSession, history: ReviewHistory): SessionReviewBoard => {
    const state = board(session);
    state.history = history;
    return state;
  };

  const setFileCollapsed = (
    session: ClientSession,
    path: string,
    collapsed: boolean,
  ): SessionReviewBoard => {
    const state = board(session);
    const entry = state.entries.get(normalizePath(path));
    if (entry !== undefined && entry.collapsed !== collapsed) {
      entry.collapsed = collapsed;
      entry.reviewedAt = collapsed ? entry.signature : null;
      entry.touch?.();
    }
    return state;
  };
  const reset = (session: ClientSession): SessionReviewBoard => {
    const state = board(session);
    state.entries.clear();
    state.files = [];
    state.label = "";
    state.added = 0;
    state.removed = 0;
    state.history = EMPTY_REVIEW_HISTORY;
    state.mode = "file";
    state.cursor = null;
    publish(session, state);
    return state;
  };

  const select = (session: ClientSession | null): void => {
    selected = session;
    if (session === null) {
      setMode("file");
      setOverview(emptyOverview());
      setCount(0);
      setContext("reviewSetActive", false);
      setContext("unifiedReviewActive", false);
    } else {
      publish(session, board(session));
    }
  };

  const enterUnified = (session: ClientSession, cursor: ReviewCursor | null): string[] => {
    const state = board(session);
    state.mode = "unified";
    if (cursor !== null) {
      state.cursor = cursor;
    }
    publish(session, state);
    return state.files.filter((file) => !file.loaded()).map((file) => file.summary().path);
  };

  const enterFile = (session: ClientSession, cursor: ReviewCursor): void => {
    const state = board(session);
    state.mode = "file";
    state.cursor = cursor;
    publish(session, state);
  };

  const setCursor = (session: ClientSession, cursor: ReviewCursor): void => {
    board(session).cursor = cursor;
  };

  const leaveUnified = (session: ClientSession): void => {
    const state = board(session);
    if (state.mode === "unified") {
      state.mode = "file";
      publish(session, state);
    }
  };

  return {
    mode,
    overview,
    count,
    board,
    select,
    setFiles,
    setDiff,
    setComments,
    setHistory,
    setFileCollapsed,
    reset,
    enterUnified,
    enterFile,
    setCursor,
    leaveUnified,
  };
}
