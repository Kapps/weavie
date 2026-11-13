// One session's PR threads and review hints as a single sequence, and the item the user is on. Each session gets
// its own reactive root, owned beside its editor contexts, so a change in one session never recomputes another's.

import {
  type Accessor,
  createEffect,
  createMemo,
  createRoot,
  createSelector,
  createSignal,
} from "solid-js";
import type { ClientSession } from "../../bridge";
import { editorContexts } from "../editor-context";
import { samePath } from "../fs-path";
import { prCommentsFor } from "../pr-comments/pr-comments-store";
import { openedHint, openHint, reviewHintsFor } from "../review-hints/review-hints-store";
import { reviewOptions } from "./review-options";
import { buildSequence, type ItemKey, type ItemLocation, type ReviewItem } from "./review-sequence";

interface CurrentThread {
  id: string;
  /** The line an editor showing the thread placed it on, once one has moved its cursor there. */
  claimed: number | null;
}

interface SessionItems {
  /** The item the last step went to, and where it left the cursor. */
  landing: { key: ItemKey; path: string; line: number } | null;
  sequence: Accessor<ReviewItem[]>;
  reviewPaths: Accessor<readonly string[]>;
  counts: Accessor<ItemCounts>;
  current: Accessor<ItemKey | null>;
  thread: Accessor<CurrentThread | null>;
  setThread: (value: CurrentThread | null) => void;
  isCurrentThread: (id: string) => boolean;
}

/** How many threads and hints each filter row covers. */
export interface ItemCounts {
  comments: number;
  hints: number;
  settled: number;
}

const roots = new WeakMap<ClientSession, SessionItems>();

const samePaths = (a: readonly string[], b: readonly string[]): boolean =>
  a.length === b.length && a.every((path, index) => path === b[index]);

function createItems(
  session: ClientSession,
  reviewPaths: () => readonly string[],
): SessionItems & { dispose(): void } {
  return createRoot((dispose) => {
    const threads = () => prCommentsFor(session)?.set?.threads ?? [];
    const hints = () => reviewHintsFor(session) ?? [];
    // A review push rebuilds every file summary; the sequence only cares when the order of paths changes.
    const paths = createMemo(reviewPaths, [], { equals: samePaths });
    const sequence = createMemo(() => {
      const options = reviewOptions();
      return buildSequence(threads(), hints(), paths(), {
        comments: options.reviewComments,
        hints: options.reviewHints,
        settled: options.reviewStepSettled,
      });
    });
    const counts = createMemo(() => {
      const settled = threads().filter((thread) => thread.resolved || thread.outdated).length;
      return { comments: threads().length - settled, hints: hints().length, settled };
    });
    const [thread, setThread] = createSignal<CurrentThread | null>(null);
    const current = createMemo((): ItemKey | null => {
      const open = openedHint(session);
      if (open?.pinned) return `hint:${open.id}`;
      const id = thread()?.id;
      return id === undefined ? null : `thread:${id}`;
    });
    // Pinning a hint (a step or a click) makes it the current item in place of a thread.
    createEffect(() => {
      if (openedHint(session)?.pinned) setThread(null);
    });
    // A thread that leaves the sequence (resolved, filtered out, gone from the PR) is no longer current.
    createEffect(() => {
      const id = thread()?.id;
      if (id !== undefined && !sequence().some((item) => item.key === `thread:${id}`))
        setThread(null);
    });
    const isCurrentThread = createSelector(() => thread()?.id);
    return {
      landing: null,
      sequence,
      reviewPaths: paths,
      counts,
      current,
      thread,
      setThread,
      isCurrentThread,
      dispose,
    };
  });
}

export const reviewItems = {
  /** Owns `session`'s sequence, ordered by the review's files; returns its disposer. */
  own(session: ClientSession, reviewPaths: () => readonly string[]): () => void {
    const items = createItems(session, reviewPaths);
    roots.set(session, items);
    return () => {
      if (roots.get(session) === items) roots.delete(session);
      items.dispose();
    };
  },
  sequence: (session: ClientSession): readonly ReviewItem[] => roots.get(session)?.sequence() ?? [],
  reviewPaths: (session: ClientSession): readonly string[] =>
    roots.get(session)?.reviewPaths() ?? [],
  counts: (session: ClientSession): ItemCounts =>
    roots.get(session)?.counts() ?? { comments: 0, hints: 0, settled: 0 },
  current: (session: ClientSession): ItemKey | null => roots.get(session)?.current() ?? null,
  /** `item`'s 1-based place in the sequence (null when it has none) and the sequence's length. */
  position(session: ClientSession, item: ItemKey | null): { index: number | null; total: number } {
    const sequence = reviewItems.sequence(session);
    const index = item === null ? -1 : sequence.findIndex((entry) => entry.key === item);
    return { index: index < 0 ? null : index + 1, total: sequence.length };
  },
  /** Makes `key` the current item: a hint opens its pinned popover, a thread is outlined. */
  setCurrent(session: ClientSession, key: ItemKey | null): void {
    const items = roots.get(session);
    if (key?.startsWith("hint:")) {
      openHint(session, { id: Number(key.slice(5)), owner: null, pinned: true });
      return;
    }
    if (openedHint(session) !== null) openHint(session, null);
    items?.setThread(key === null ? null : { id: key.slice(7), claimed: null });
  },
  isCurrentThread: (session: ClientSession, id: string): boolean =>
    roots.get(session)?.isCurrentThread(id) ?? false,
  /** The current thread no displayed editor has moved its cursor to yet. */
  unclaimedThread(session: ClientSession): string | null {
    const thread = roots.get(session)?.thread();
    return thread != null && thread.claimed === null ? thread.id : null;
  },
  claimThread(session: ClientSession, line: number): void {
    const items = roots.get(session);
    const thread = items?.thread();
    if (thread != null) items?.setThread({ ...thread, claimed: line });
  },
  /**
   * Where a displayed editor placed the current item, following local edits; null until one has. The item's own
   * line is numbered against the text it was made on.
   */
  claimedLine(session: ClientSession): number | null {
    const open = openedHint(session);
    if (open?.pinned && open.owner !== null)
      return editorContexts.fromEditor(open.owner)?.reviewHints.lineOf(open.id) ?? null;
    return roots.get(session)?.thread()?.claimed ?? null;
  },
  /** Records where a step left the cursor: on `key`'s line of `path`. */
  land(session: ClientSession, key: ItemKey, path: string, line: number): void {
    const items = roots.get(session);
    if (items !== undefined) items.landing = { key, path, line };
  },
  /**
   * Where a step from `location` starts: the item the last step went to while the cursor is still on its line and the
   * item is still listed, so Escape followed by a step moves on rather than reopening it; otherwise the location
   * itself, so a step after a dismiss goes on to the item after the one dismissed.
   */
  resumeFrom(session: ClientSession, location: ItemLocation): ItemKey | ItemLocation {
    const landing = roots.get(session)?.landing;
    return landing != null &&
      landing.line === location.line &&
      samePath(landing.path, location.path) &&
      reviewItems.sequence(session).some((item) => item.key === landing.key)
      ? landing.key
      : location;
  },
  /** Clears a current thread; false when there was none. */
  clearThread(session: ClientSession): boolean {
    const items = roots.get(session);
    if (items?.thread() == null) return false;
    items.setThread(null);
    return true;
  },
};
