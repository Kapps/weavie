import { observeElementOffset, type Virtualizer } from "@tanstack/solid-virtual";
import {
  type Accessor,
  batch,
  createEffect,
  createMemo,
  createSignal,
  on,
  onCleanup,
  onMount,
} from "solid-js";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { createAgentPaneGesture } from "./AgentPaneGesture";
import type { AgentPaneModel } from "./AgentPaneModel";
import type { AgentTranscriptSnapshot } from "./AgentPaneTranscriptTypes";
import { withLivePending } from "./AgentTranscriptSnapshot";

type PaneVirtualizer = Virtualizer<HTMLDivElement, HTMLDivElement>;

/** Where the reader was when the pane last unmounted. */
export interface AgentPaneScrollState {
  following: boolean;
  shown: AgentTranscriptSnapshot;
  unseen: boolean;
  watched: { id: string; topSeen: boolean } | null;
}

// The scroller is bottom-origin (column-reverse): scrollTop is 0 at the latest output and negative above it,
// so output arriving above the reader never moves what is on screen.
export function createAgentPaneScroll(
  model: AgentPaneModel,
  body: Accessor<HTMLDivElement | undefined>,
  saved: AgentPaneScrollState | undefined,
  virtualizer: () => PaneVirtualizer,
  virtualIndex: (index: number) => number,
  turnNavigable: Accessor<boolean>,
  wheel: { cancel: () => void },
) {
  const [following, setFollowingSignal] = createSignal(saved?.following ?? true);
  const [unseen, setUnseen] = createSignal(
    saved !== undefined &&
      (saved.unseen || (!saved.following && saved.shown !== model.transcript())),
  );
  // The transcript a touch gesture is reading, held still until the gesture ends.
  const [held, setHeld] = createSignal<AgentTranscriptSnapshot | null>(
    saved !== undefined && !saved.following ? saved.shown : null,
  );
  const [turnStartAbove, setTurnStartAbove] = createSignal(false);
  const shown = createMemo(() => {
    const frozen = held();
    return frozen === null ? model.transcript() : withLivePending(frozen, model.transcript());
  });
  const liveResultIndex = (): number | null => (model.live() ? shown().latestResultIndex : null);
  // The topmost visible row, where the reader sees it, and the scroll position and viewport it was seen in.
  let anchor: { height: number; id: string; scroll: number; top: number } | null = null;
  // The position the pane last wrote, so its scroll event isn't mistaken for the reader moving.
  let written: number | null = null;
  let pinned = false;
  // The live reply the pin watches; one already above the viewport when following began never pins.
  let watched = saved?.watched ?? null;
  let userChange = false;
  let notifyOffset = (): void => {};
  const gesture = createAgentPaneGesture(body, () => syncHold());

  const element = (): HTMLDivElement => body()!;
  const topOf = (row: HTMLElement): number =>
    row.getBoundingClientRect().top - element().getBoundingClientRect().top;
  const rowElement = (id: string): HTMLElement | null =>
    element().querySelector<HTMLElement>(
      `.agent-virtual-row[data-transcript-entry="${CSS.escape(id)}"]`,
    );
  const followThreshold = (): number =>
    Math.ceil(Number.parseFloat(getComputedStyle(element()).lineHeight) * 3);
  const nearBottom = (): boolean => -element().scrollTop <= followThreshold();

  const write = (top: number): void => {
    const before = element().scrollTop;
    element().scrollTop = top;
    written = element().scrollTop;
    // The pane's own writes are not the reader moving, so the anchor's view moves with them.
    if (anchor !== null) anchor.scroll += written - before;
    notifyOffset();
  };

  const capture = (): void => {
    const top = element().getBoundingClientRect().top;
    for (const row of element().querySelectorAll<HTMLElement>(".agent-virtual-row")) {
      if (row.getBoundingClientRect().bottom > top) {
        anchor = {
          height: element().clientHeight,
          id: row.dataset.transcriptEntry!,
          scroll: element().scrollTop,
          top: topOf(row),
        };
        return;
      }
    }
    anchor = null;
  };

  // Puts the anchor back where the reader sees it after the transcript below it changed size. A resized
  // viewport keeps its bottom edge, like a chat; so does a top the engine clamped after history shrank.
  const settle = (): void => {
    const row = anchor === null ? null : rowElement(anchor.id);
    if (anchor === null || row === null) return;
    const scrolled = element().scrollTop - anchor.scroll;
    // Within a pixel: the transcript's height is fractional but scrollHeight whole.
    const top = element().clientHeight - element().scrollHeight + 1;
    if (
      anchor.height !== element().clientHeight ||
      (scrolled !== 0 && element().scrollTop <= top)
    ) {
      capture();
      return;
    }
    const shift = Math.trunc(topOf(row) - anchor.top + scrolled);
    if (shift !== 0) write(element().scrollTop + shift);
  };

  const syncHold = (): void => {
    const holding = gesture.touching() && !following();
    if (holding && held() === null) setHeld(shown());
    else if (!holding && held() !== null) setHeld(null);
  };

  const setFollowing = (value: boolean): void => {
    if (!value) capture();
    if (value && watched !== null) watched.topSeen = false;
    batch(() => {
      setFollowingSignal(value);
      if (value) setUnseen(false);
      syncHold();
    });
  };

  // A live reply whose top scrolls out of view while following stops there, so it is read from its start.
  const pin = (): void => {
    const index = liveResultIndex();
    const id = index === null ? null : shown().entries[index]!.id;
    const row = id === null ? null : rowElement(id);
    if (id === null || row === null) return;
    if (watched?.id !== id) watched = { id, topSeen: true };
    const overshoot = topOf(row);
    if (overshoot > 0) {
      if (overshoot < element().clientHeight) watched.topSeen = true;
      return;
    }
    if (!watched.topSeen) return;
    pinned = true;
    if (!gesture.active()) write(element().scrollTop + overshoot);
    setFollowing(false);
  };

  const updateTurnStartAbove = (): void => {
    const index = shown().agentTurnStartIndex;
    const row = index === null ? undefined : virtualizer().measurementsCache[virtualIndex(index)];
    setTurnStartAbove(
      turnNavigable() &&
        row !== undefined &&
        row.end > element().clientHeight - element().scrollTop + 1,
    );
  };

  // After the rendered rows change: a reader who isn't following stays still; a long live reply pins.
  const afterLayout = (): void => {
    if (body()?.isConnected !== true) return;
    if (!following()) {
      if (!gesture.touching()) settle();
    } else {
      pin();
    }
    updateTurnStartAbove();
  };

  const jumpToLatest = (): boolean => {
    // Asking for the latest output stops a reply already in view from pinning at its top.
    if (watched !== null) watched.topSeen = false;
    if (following() && nearBottom()) return false;
    const momentum = gesture.touching();
    wheel.cancel();
    gesture.reset();
    pinned = false;
    setFollowing(true);
    write(0);
    if (momentum) {
      // iOS ignores scroll writes during momentum; suspending scrolling for a frame stops it first.
      const scroller = element();
      scroller.style.overflowY = "hidden";
      requestAnimationFrame(() => {
        scroller.style.overflowY = "";
        if (following() && scroller.isConnected) write(0);
      });
    }
    return true;
  };

  // The virtualizer re-aims each frame while the rows it scrolls past are measured.
  const reveal = (index: number): void => {
    wheel.cancel();
    pinned = false;
    setFollowing(false);
    virtualizer().scrollToIndex(virtualIndex(index), { align: "end", behavior: "auto" });
  };

  const jumpToTurn = (): boolean => {
    const index = shown().agentTurnStartIndex;
    if (!turnNavigable() || index === null) return false;
    const previous = element().scrollTop;
    reveal(index);
    return Math.abs(element().scrollTop - previous) >= 1;
  };

  // New output is a new or changed latest row; status changes further up are not announced.
  createEffect(
    on(
      model.transcript,
      (next, previous) => {
        const latest = next.entries.at(-1);
        if (!following() && !userChange && latest !== previous?.entries.at(-1)) setUnseen(true);
      },
      { defer: true },
    ),
  );
  // New output while following lands at the bottom.
  createEffect(
    on(
      shown,
      () => {
        if (following() && !gesture.active() && element().scrollTop !== 0) write(0);
      },
      { defer: true },
    ),
  );
  createEffect(on(turnNavigable, () => body() !== undefined && updateTurnStartAbove()));
  // A request needs the user, so it releases the automatic pin; a position the reader chose is kept.
  createEffect(
    on(model.keyboardRequestKey, (key) => {
      if (key !== null && pinned && !following()) jumpToLatest();
    }),
  );

  onMount(() => {
    if (!following()) {
      // A remount shows the transcript as the reader left it, then applies what arrived since.
      requestAnimationFrame(() => {
        if (body()?.isConnected !== true) return;
        capture();
        syncHold();
      });
    }
    const targetsSession = (target: unknown): boolean => target === model.session;
    const unregisterTurn = registerCommand(
      CommandIds.agentJumpToTurn,
      (_args, context) => targetsSession(context.session) && jumpToTurn(),
    );
    const unregisterLatest = registerCommand(
      CommandIds.agentJumpToLatest,
      (_args, context) => targetsSession(context.session) && jumpToLatest(),
    );
    onCleanup(() => {
      unregisterTurn();
      unregisterLatest();
    });
  });

  return {
    following,
    jumpToLatest,
    jumpToStart: (): void => reveal(0),
    jumpToTurn,
    shown,
    turnStartAbove,
    unseen,
    anchorId: (): string | null => anchor?.id ?? null,
    /** Applies a change the reader made: never unseen output, and it keeps what they see still. */
    applyUserChange(change: () => void): void {
      if (following()) setFollowing(false);
      userChange = true;
      try {
        change();
      } finally {
        userChange = false;
      }
    },
    observeOffset(instance: PaneVirtualizer, notify: (offset: number, scrolling: boolean) => void) {
      notifyOffset = () => notify(Math.max(0, -element().scrollTop), instance.isScrolling);
      return observeElementOffset(instance, (offset, scrolling) =>
        notify(Math.max(0, -offset), scrolling),
      );
    },
    onScroll(): void {
      gesture.noteScroll();
      const moved = element().scrollTop !== written;
      written = null;
      if (moved && gesture.active()) {
        pinned = false;
        if (nearBottom() !== following()) setFollowing(!following());
      }
      if (moved && !following()) capture();
      updateTurnStartAbove();
    },
    /** The rendered rows were mounted, measured, or changed, which can move what is below the reader. */
    onLayout: afterLayout,
    onWheelIntent(delta: number): void {
      pinned = false;
      if (delta < 0 && following()) setFollowing(false);
    },
    onWheelSettled(): void {
      if (nearBottom()) setFollowing(true);
    },
    scrollTo(offset: number): void {
      element().scrollTop = -offset;
    },
    snapshot: (): AgentPaneScrollState => ({
      following: following(),
      shown: shown(),
      unseen: unseen(),
      watched,
    }),
  };
}
