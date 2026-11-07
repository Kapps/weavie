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
  untrack,
} from "solid-js";
import { isAutoscrolling } from "../chrome/middle-click-autoscroll";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { onFontsChanged } from "../fonts";
import { createReadingAnchor } from "./AgentPaneAnchor";
import { createAgentPaneGesture } from "./AgentPaneGesture";
import type { createAgentPaneLayout } from "./AgentPaneLayout";
import type { AgentPaneModel } from "./AgentPaneModel";
import type { AgentTranscriptSnapshot } from "./AgentPaneTranscriptTypes";
import { createAgentPaneWheel } from "./AgentPaneWheel";
import { withLivePending } from "./AgentTranscriptSnapshot";

type PaneVirtualizer = Virtualizer<HTMLDivElement, HTMLDivElement>;

/** Where the reader was when the pane last unmounted. */
export interface AgentPaneScrollState {
  following: boolean;
  /** Distance from the bottom of the transcript to the bottom of the viewport. */
  offset: number;
  /** The model's `latestOutput` when the reader stopped following. */
  seen: number;
  shown: AgentTranscriptSnapshot;
  topSeen: boolean;
  watchedId: string | null;
}

// The scroller is bottom-origin (column-reverse), so output arriving above the reader never moves what is on
// screen. The pane owns the scroll position: it tracks what it wrote and what scroll events report, and works
// out every correction from the virtualizer's measurements, so scrolling never reads layout.
export function createAgentPaneScroll(
  model: AgentPaneModel,
  body: Accessor<HTMLDivElement | undefined>,
  saved: AgentPaneScrollState | undefined,
  virtualizer: () => PaneVirtualizer,
  layout: () => ReturnType<typeof createAgentPaneLayout>,
) {
  const [following, setFollowingSignal] = createSignal(saved?.following ?? true);
  const [seen, setSeen] = createSignal(saved?.seen ?? model.latestOutput());
  // The transcript a touch gesture is reading, held still until the gesture ends.
  const [held, setHeld] = createSignal<AgentTranscriptSnapshot | null>(
    saved !== undefined && !saved.following ? saved.shown : null,
  );
  const [turnStartAbove, setTurnStartAbove] = createSignal(false);
  const shown = createMemo(() => {
    const frozen = held();
    return frozen === null ? model.transcript() : withLivePending(frozen, model.transcript());
  });
  const turnNavigable = createMemo(() => !shown().turnActive && shown().agentTurnStartId !== null);
  let position = saved?.offset ?? 0;
  let viewport = 0;
  let lineHeight = 0;
  let pinned = false;
  // The live reply the pin watches; one already above the viewport when following began never pins.
  let watchedId = saved?.watchedId ?? null;
  let topSeen = saved?.topSeen ?? false;
  let notifyOffset = (): void => {};

  const element = (): HTMLDivElement => body()!;
  const anchor = createReadingAnchor(virtualizer, (id) => layout().rowOf(id));
  const maxPosition = (): number => Math.max(0, virtualizer().getTotalSize() - viewport);
  const clamped = (to: number): number => Math.max(0, Math.min(maxPosition(), to));
  const nearBottom = (): boolean => position <= Math.ceil(lineHeight * 3);
  // The reader is moving the pane, so the pane doesn't write over them.
  const driving = (): boolean =>
    gesture.touching() || wheel.isActive() || isAutoscrolling(element());
  const measureLineHeight = (): void => {
    lineHeight = Number.parseFloat(getComputedStyle(element()).lineHeight);
  };

  const place = (to: number): void => {
    position = clamped(to);
    element().scrollTop = -position;
    updateTurnStartAbove();
  };

  const pickAnchor = (): void => anchor.pick(position, viewport);
  // Keeps the anchor where the reader sees it after the transcript below it changed size.
  const keepAnchor = (): void => {
    const to = anchor.target();
    if (to === null) {
      pickAnchor();
      return;
    }
    if (Math.abs(to - position) < 0.5) return;
    place(to);
    if (position !== to) pickAnchor();
    notifyOffset();
  };

  // Moves where the reader asked to go, so what is in view there is what stays still.
  const moveTo = (to: number): void => {
    place(to);
    if (!following()) pickAnchor();
    notifyOffset();
  };

  const syncHold = (): void => {
    const holding = gesture.touching() && !following();
    if (holding && held() === null) setHeld(shown());
    else if (!holding && held() !== null) setHeld(null);
  };
  const gesture = createAgentPaneGesture(body, syncHold);

  const setFollowing = (value: boolean): void => {
    if (value === following()) return;
    if (value) topSeen = false;
    if (!value) pickAnchor();
    batch(() => {
      setFollowingSignal(value);
      if (!value) setSeen(untrack(model.latestOutput));
      syncHold();
    });
  };

  // A live reply whose top scrolls out of view while following stops there, so it is read from its start. One
  // pushed out by the viewport shrinking (the composer, a keyboard) keeps following, bottom-anchored like a chat.
  const pin = (resized: boolean): void => {
    const index = model.live() ? shown().latestResultIndex : null;
    const row = layout().rowAt(index);
    // Decided on measured sizes, never on estimates.
    if (index === null || row === undefined || !anchor.measuredThrough(row.index)) return;
    const { id } = shown().entries[index]!;
    if (watchedId !== id) {
      watchedId = id;
      topSeen = true;
    }
    const overshoot = viewport - (row.end - position);
    if (overshoot > 0) {
      if (overshoot < viewport) topSeen = true;
      return;
    }
    if (resized) topSeen = false;
    if (!topSeen) return;
    pinned = true;
    const placing = !driving();
    if (placing) place(position - overshoot);
    setFollowing(false);
    if (placing) anchor.reveal(row, position);
    notifyOffset();
  };

  const updateTurnStartAbove = (): void => {
    const row = layout().rowAt(shown().agentTurnStartIndex);
    setTurnStartAbove(turnNavigable() && row !== undefined && row.end > position + viewport + 1);
  };

  // After the measured transcript changed: a reader who isn't following stays still; a long live reply pins.
  const settle = (resized: boolean): void => {
    if (viewport === 0 || !element().isConnected) return;
    // Nothing to scroll means everything is in view.
    if (virtualizer().getTotalSize() <= viewport) setFollowing(true);
    if (following()) pin(resized);
    else if (gesture.touching() || isAutoscrolling(element())) pickAnchor();
    else keepAnchor();
    updateTurnStartAbove();
  };

  // A scroll the pane didn't write is the reader moving: wheel, keys, touch, scrollbar, focus, or find.
  const observed = (to: number): void => {
    // Layout only ever clamps toward the bottom, onto the top edge; a finger's scroll toward it can't land there.
    gesture.scrolled(
      () =>
        to > position + 0.5 ||
        (to < position - 0.5 && to < element().scrollHeight - element().clientHeight - 1),
    );
    if (Math.abs(to - position) <= 1) return;
    position = clamped(to);
    pinned = false;
    setFollowing(!wheel.isActive() && nearBottom());
    if (!following()) pickAnchor();
    updateTurnStartAbove();
  };

  const jumpToLatest = (): boolean => {
    // Asking for the latest output stops a reply already in view from pinning at its top.
    topSeen = false;
    if (following() && nearBottom()) return false;
    const momentum = gesture.touching();
    wheel.cancel();
    gesture.reset();
    pinned = false;
    setFollowing(true);
    moveTo(0);
    if (momentum) {
      // iOS ignores scroll writes during momentum; suspending scrolling for a frame stops it first.
      const scroller = element();
      scroller.style.overflowY = "hidden";
      requestAnimationFrame(() => {
        scroller.style.overflowY = "";
        if (following() && scroller.isConnected) moveTo(0);
      });
    }
    return true;
  };

  // Puts a row's top at the viewport top and keeps it there while the rows around it are measured.
  const reveal = (index: number): void => {
    const row = layout().rowAt(index);
    if (row === undefined) return;
    wheel.cancel();
    pinned = false;
    setFollowing(false);
    place(row.end - viewport);
    anchor.reveal(row, position);
    notifyOffset();
  };

  const jumpToTurn = (): boolean => {
    const index = shown().agentTurnStartIndex;
    if (!turnNavigable() || index === null) return false;
    const previous = position;
    reveal(index);
    return Math.abs(position - previous) >= 1;
  };

  const wheel = createAgentPaneWheel(body, {
    scrollBy(delta) {
      const max = maxPosition();
      const to = position - delta;
      pinned = false;
      moveTo(to);
      if (delta < 0 && max > 0 && to >= max) return "start";
      return delta > 0 && to <= 0 ? "end" : null;
    },
    intent(delta) {
      pinned = false;
      if (delta < 0) setFollowing(false);
    },
    settled() {
      if (nearBottom()) setFollowing(true);
    },
    start: () => reveal(0),
    end: jumpToLatest,
    lineHeight: () => lineHeight,
    viewportHeight: () => viewport,
  });

  // New output while following lands at the bottom, unless the reader is moving.
  createEffect(
    on(
      shown,
      () => {
        if (following() && position !== 0 && !driving()) moveTo(0);
      },
      { defer: true },
    ),
  );
  createEffect(on(turnNavigable, () => viewport > 0 && updateTurnStartAbove()));
  // A request needs the user, so it releases the automatic pin; a position the reader chose is kept.
  createEffect(
    on(model.keyboardRequestKey, (key) => {
      if (key !== null && pinned && !following()) jumpToLatest();
    }),
  );

  onMount(() => {
    onCleanup(onFontsChanged(measureLineHeight));
    const register = (id: string, run: () => boolean) =>
      onCleanup(
        registerCommand(id, (_args, context) => context.session === model.session && run()),
      );
    register(CommandIds.agentJumpToTurn, jumpToTurn);
    register(CommandIds.agentJumpToLatest, jumpToLatest);
  });

  return {
    following,
    jumpToLatest,
    jumpToTurn,
    shown,
    turnNavigable,
    turnStartAbove,
    noteMeasured: anchor.noteMeasured,
    forgetMeasured: anchor.forget,
    unseen: (): boolean => !following() && model.latestOutput() !== seen(),
    observeOffset(instance: PaneVirtualizer, notify: (offset: number, scrolling: boolean) => void) {
      notifyOffset = () => notify(position, instance.isScrolling);
      return observeElementOffset(instance, (offset, scrolling) => {
        observed(-offset);
        notify(Math.max(0, -offset), scrolling);
      });
    },
    /** The rendered rows were mounted, measured, or changed, which can move what is below the reader. */
    onLayout: (): void => settle(false),
    /**
     * The viewport's height, from a resize observer; a resized viewport keeps its bottom edge, like a chat, except
     * that a reply placed at the top to be read from its start stays at the top.
     */
    onResize(height: number): void {
      viewport = height;
      measureLineHeight();
      if (!following() && !anchor.keepTop(viewport)) pickAnchor();
      // A remount shows the transcript as the reader left it, then applies what arrived once the viewport is known.
      syncHold();
      settle(true);
    },
    scrollTo: moveTo,
    snapshot: (): AgentPaneScrollState => ({
      following: following(),
      offset: position,
      seen: seen(),
      shown: shown(),
      topSeen,
      watchedId,
    }),
  };
}
