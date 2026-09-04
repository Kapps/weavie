import type { Virtualizer } from "@tanstack/solid-virtual";
import { type Accessor, createEffect, createSignal, on, onCleanup, onMount } from "solid-js";
import type { ClientSession } from "../bridge";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";

const turnStartAlignmentTolerance = 1;

export function createAgentPaneScroll(
  session: ClientSession,
  body: Accessor<HTMLDivElement | undefined>,
  virtualizer: Virtualizer<HTMLDivElement, HTMLDivElement>,
  turnStartIndex: Accessor<number | null>,
  turnNavigable: Accessor<boolean>,
  revision: Accessor<number>,
  initiallyFollowingLatest: boolean,
  wheel: { cancel: () => void; isActive: () => boolean },
) {
  let bottomCorrectionScheduled = false;
  let controllerScrolls: Array<{ top: number }> = [];
  let scrollScheduled = false;
  let viewportHeight = 0;
  let viewportWidth = 0;
  const [followingLatest, setFollowingLatest] = createSignal(initiallyFollowingLatest);
  const [agentTurnStartAbove, setAgentTurnStartAbove] = createSignal(false);

  const followThreshold = (): number => {
    const element = body();
    return element === undefined
      ? 0
      : Math.ceil(Number.parseFloat(getComputedStyle(element).lineHeight) * 3);
  };

  const isNearBottom = (): boolean => {
    const element = body();
    return (
      element !== undefined &&
      element.scrollHeight - element.scrollTop - element.clientHeight <= followThreshold()
    );
  };

  const updateAgentTurnStartPosition = (): void => {
    const index = turnStartIndex();
    if (index === null) {
      setAgentTurnStartAbove(false);
      return;
    }
    virtualizer.getTotalSize();
    const start = virtualizer.measurementsCache[index]?.start;
    // Sub-pixel tolerance: scrollOffset and the cached start can settle a fraction of a pixel
    // apart (e.g. 745.671875 vs 746) even when the turn start is exactly at the viewport top,
    // which without slack flips this above-the-fold long after a jump with nothing left to
    // correct it.
    setAgentTurnStartAbove(
      start !== undefined && start + turnStartAlignmentTolerance < (virtualizer.scrollOffset ?? 0),
    );
  };

  const noteControllerScroll = (top: number): void => {
    const element = body();
    if (element === undefined) {
      return;
    }
    const maximum = Math.max(element.scrollHeight - element.clientHeight, 0);
    const assigned = { top: Math.min(Math.max(top, 0), maximum) };
    controllerScrolls.push(assigned);
    requestAnimationFrame(() => {
      controllerScrolls = controllerScrolls.filter((candidate) => candidate !== assigned);
    });
  };

  const assign = (action: () => void, followsLatest: boolean): void => {
    const element = body();
    if (element === undefined) {
      return;
    }
    wheel.cancel();
    setFollowingLatest(followsLatest);
    action();
    noteControllerScroll(element.scrollTop);
    updateAgentTurnStartPosition();
  };

  const assignBottom = (): void =>
    assign(() => {
      const element = body();
      if (element !== undefined) {
        element.scrollTop = element.scrollHeight;
      }
    }, true);

  const scrollToBottom = (): void => {
    if (scrollScheduled) {
      return;
    }
    scrollScheduled = true;
    requestAnimationFrame(() => {
      scrollScheduled = false;
      if (followingLatest()) {
        assignBottom();
      }
    });
  };

  const jumpToTurn = (): boolean => {
    const index = turnStartIndex();
    const element = body();
    if (!turnNavigable() || index === null || element === undefined) {
      return false;
    }
    const previous = element.scrollTop;
    assign(() => virtualizer.scrollToIndex(index, { align: "start", behavior: "auto" }), false);
    setAgentTurnStartAbove(false);
    return Math.abs(element.scrollTop - previous) >= 1;
  };

  const jumpToLatest = (): boolean => {
    if (followingLatest() && isNearBottom()) {
      return false;
    }
    assignBottom();
    return true;
  };

  const onViewportResize = (): void => {
    const height = body()?.clientHeight ?? 0;
    const width = body()?.clientWidth ?? 0;
    if (height === viewportHeight && width === viewportWidth) return;
    viewportHeight = height;
    viewportWidth = width;
    if (followingLatest()) assignBottom();
  };

  // Measurement anchoring also emits scroll events; every unowned scroll is the user's intent.
  const onScroll = (): void => {
    const element = body();
    if (element === undefined) {
      return;
    }
    // A viewport resize can clamp scrollTop and emit scroll before ResizeObserver runs.
    onViewportResize();
    const assigned = controllerScrolls.findIndex(
      (candidate) => Math.abs(candidate.top - element.scrollTop) < 1.5,
    );
    if (assigned >= 0) {
      controllerScrolls.splice(assigned, 1);
    } else {
      controllerScrolls = [];
      setFollowingLatest(!wheel.isActive() && isNearBottom());
    }
    updateAgentTurnStartPosition();
  };

  const onVirtualizerChange = (sync: boolean): void => {
    if (followingLatest() && !sync && !bottomCorrectionScheduled) {
      bottomCorrectionScheduled = true;
      requestAnimationFrame(() => {
        bottomCorrectionScheduled = false;
        if (followingLatest() && !isNearBottom()) {
          assignBottom();
        }
      });
    }
    updateAgentTurnStartPosition();
  };

  createEffect(
    on(
      revision,
      () => {
        if (followingLatest()) {
          scrollToBottom();
        } else {
          updateAgentTurnStartPosition();
        }
      },
      { defer: true },
    ),
  );

  createEffect(
    on(turnNavigable, (navigable) => {
      if (navigable) {
        updateAgentTurnStartPosition();
      } else {
        setAgentTurnStartAbove(false);
      }
    }),
  );

  onMount(() => {
    if (followingLatest()) {
      scrollToBottom();
    } else {
      updateAgentTurnStartPosition();
    }
    const targetsSession = (target: ClientSession | null): boolean => target === session;
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
    agentTurnStartAbove,
    followingLatest,
    followIfNearBottom: (): void => {
      if (isNearBottom()) {
        setFollowingLatest(true);
      }
    },
    jumpToLatest,
    jumpToTurn,
    noteControllerScroll,
    onScroll,
    onWheelIntent: (): void => {
      controllerScrolls = [];
      setFollowingLatest(false);
    },
    onWheelSettled: (): void => {
      setFollowingLatest(isNearBottom());
      updateAgentTurnStartPosition();
    },
    onViewportResize,
    onVirtualizerChange,
  };
}
