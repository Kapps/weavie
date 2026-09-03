import type { Virtualizer } from "@tanstack/solid-virtual";
import { type Accessor, createEffect, createSignal, on, onCleanup, onMount } from "solid-js";
import type { ClientSession } from "../bridge";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";

const turnStartAlignmentTolerance = 1;
const bottomAlignmentTolerance = 1;
export type FollowPosition = "bottom" | "near" | "detached";

/** True when the viewport is at the latest content, allowing only sub-pixel layout rounding. */
export function isAlignedToBottom(
  scrollHeight: number,
  scrollTop: number,
  clientHeight: number,
): boolean {
  return scrollHeight - scrollTop - clientHeight <= bottomAlignmentTolerance;
}

export function followPositionForDistance(distance: number, threshold: number): FollowPosition {
  return distance <= bottomAlignmentTolerance
    ? "bottom"
    : distance <= threshold
      ? "near"
      : "detached";
}

export function needsBottomCorrection(position: FollowPosition, aligned: boolean): boolean {
  return position === "bottom" && !aligned;
}

export function followPositionAfterRevision(position: FollowPosition): FollowPosition {
  return position === "detached" ? "detached" : "bottom";
}

export function createAgentPaneScroll(
  session: ClientSession,
  body: Accessor<HTMLDivElement | undefined>,
  virtualizer: Virtualizer<HTMLDivElement, HTMLDivElement>,
  turnStartIndex: Accessor<number | null>,
  turnNavigable: Accessor<boolean>,
  revision: Accessor<number>,
  initialFollowPosition: FollowPosition,
) {
  let bottomCorrectionScheduled = false;
  let controllerScrolls: Array<{ top: number }> = [];
  let scrollScheduled = false;
  const [followPosition, setFollowPosition] = createSignal(initialFollowPosition);
  const followingLatest = (): boolean => followPosition() !== "detached";
  const [agentTurnStartAbove, setAgentTurnStartAbove] = createSignal(false);

  const followThreshold = (): number => {
    const element = body();
    return element === undefined
      ? 0
      : Math.ceil(Number.parseFloat(getComputedStyle(element).lineHeight) * 3);
  };

  const positionForGeometry = (): FollowPosition => {
    const element = body();
    return element === undefined
      ? "detached"
      : followPositionForDistance(
          element.scrollHeight - element.scrollTop - element.clientHeight,
          followThreshold(),
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

  const assign = (action: () => void, position: FollowPosition): void => {
    const element = body();
    if (element === undefined) {
      return;
    }
    setFollowPosition(position);
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
    }, "bottom");

  const scrollToBottom = (): void => {
    if (scrollScheduled) {
      return;
    }
    scrollScheduled = true;
    requestAnimationFrame(() => {
      scrollScheduled = false;
      if (followPosition() === "bottom") {
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
    assign(
      () => virtualizer.scrollToIndex(index, { align: "start", behavior: "auto" }),
      "detached",
    );
    setAgentTurnStartAbove(false);
    return Math.abs(element.scrollTop - previous) >= 1;
  };

  const jumpToLatest = (): boolean => {
    if (followingLatest() && positionForGeometry() !== "detached") {
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
      setFollowPosition(positionForGeometry());
    }
    updateAgentTurnStartPosition();
  };

  const onVirtualizerChange = (sync: boolean): void => {
    if (followPosition() === "bottom" && !sync && !bottomCorrectionScheduled) {
      bottomCorrectionScheduled = true;
      requestAnimationFrame(() => {
        bottomCorrectionScheduled = false;
        const element = body();
        if (
          element !== undefined &&
          needsBottomCorrection(
            followPosition(),
            isAlignedToBottom(element.scrollHeight, element.scrollTop, element.clientHeight),
          )
        ) {
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
        const position = followPositionAfterRevision(followPosition());
        setFollowPosition(position);
        if (position === "bottom") {
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
    followPosition,
    followingLatest,
    followIfNearBottom: (): void => {
      const position = positionForGeometry();
      if (position !== "detached") {
        setFollowPosition(position);
      }
    },
    jumpToLatest,
    jumpToTurn,
    noteControllerScroll,
    onScroll,
    onVirtualizerChange,
  };
}
