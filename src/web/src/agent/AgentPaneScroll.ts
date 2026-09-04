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
) {
  let chaseScheduled = false;
  let lastChasedScrollHeight = -1;
  let controllerScrolls: Array<{ top: number }> = [];
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

  // The virtualizer measures newly-revealed items asynchronously below the fold, so one
  // `scrollTop = scrollHeight` assignment can fall short of the eventual bottom: later rounds keep
  // growing `scrollHeight` after we've already caught up to what it reported at assignment time.
  // Chase it across frames until the height holds steady instead of trusting any single pass — or the
  // virtualizer's own notification — to be the last one.
  const scrollToBottom = (): void => {
    if (chaseScheduled) {
      return;
    }
    chaseScheduled = true;
    requestAnimationFrame(() => {
      chaseScheduled = false;
      if (!followingLatest()) {
        return;
      }
      if (!isNearBottom()) {
        assignBottom();
      }
      const height = body()?.scrollHeight ?? lastChasedScrollHeight;
      if (height !== lastChasedScrollHeight) {
        lastChasedScrollHeight = height;
        scrollToBottom();
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

  // Measurement anchoring also emits scroll events; every unowned scroll is the user's intent.
  const onScroll = (): void => {
    const element = body();
    if (element === undefined) {
      return;
    }
    const assigned = controllerScrolls.findIndex(
      (candidate) => Math.abs(candidate.top - element.scrollTop) < 1.5,
    );
    if (assigned >= 0) {
      controllerScrolls.splice(assigned, 1);
    } else {
      controllerScrolls = [];
      setFollowingLatest(isNearBottom());
    }
    updateAgentTurnStartPosition();
  };

  const onVirtualizerChange = (sync: boolean): void => {
    if (followingLatest() && !sync) {
      scrollToBottom();
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
    onVirtualizerChange,
  };
}
