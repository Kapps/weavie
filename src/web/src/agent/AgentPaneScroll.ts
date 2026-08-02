import { type Accessor, createEffect, createSignal, on, onCleanup, onMount } from "solid-js";
import type { ClientSession } from "../bridge";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";

export function createAgentPaneScroll(
  session: Accessor<ClientSession | null>,
  turnNavigable: Accessor<boolean>,
) {
  let body: HTMLDivElement | undefined;
  let scrollScheduled = false;
  let programmaticScroll = false;
  let assignedTop = 0;
  let assignedFollowingLatest = true;
  const [followingLatest, setFollowingLatest] = createSignal(true);
  const [agentTurnStartAbove, setAgentTurnStartAbove] = createSignal(false);

  const isNearBottom = (): boolean => {
    if (body === undefined) {
      return true;
    }
    const distance = body.scrollHeight - body.scrollTop - body.clientHeight;
    const lineHeight = Number.parseFloat(getComputedStyle(body).lineHeight);
    return distance <= Math.ceil(lineHeight * 3);
  };

  const agentTurnStart = (): HTMLElement | null =>
    body?.querySelector<HTMLElement>("[data-agent-turn-output-start]") ?? null;

  const updateAgentTurnStartPosition = (): void => {
    const start = agentTurnStart();
    setAgentTurnStartAbove(
      body !== undefined &&
        start !== null &&
        start.getBoundingClientRect().top < body.getBoundingClientRect().top,
    );
  };

  const assignScrollTop = (top: number, followsLatest: boolean): void => {
    if (body === undefined) {
      return;
    }
    const previous = body.scrollTop;
    body.scrollTop = top;
    assignedTop = body.scrollTop;
    assignedFollowingLatest = followsLatest;
    programmaticScroll ||= assignedTop !== previous;
    updateAgentTurnStartPosition();
  };

  const assignBottom = (): void => assignScrollTop(body?.scrollHeight ?? 0, true);

  const scrollToBottom = (): void => {
    if (scrollScheduled) {
      return;
    }
    scrollScheduled = true;
    requestAnimationFrame(() => {
      scrollScheduled = false;
      if (body === undefined || !followingLatest()) {
        return;
      }
      assignBottom();
    });
  };

  const jumpToTurn = (): boolean => {
    const start = agentTurnStart();
    if (!turnNavigable() || body === undefined || start === null) {
      return false;
    }
    const markerTop =
      body.scrollTop + start.getBoundingClientRect().top - body.getBoundingClientRect().top;
    const top = Math.min(Math.max(markerTop, 0), body.scrollHeight - body.clientHeight);
    if (Math.abs(body.scrollTop - top) < 1) {
      return false;
    }
    setFollowingLatest(false);
    assignScrollTop(top, false);
    return true;
  };

  const jumpToLatest = (): boolean => {
    if (body === undefined || (followingLatest() && isNearBottom())) {
      return false;
    }
    setFollowingLatest(true);
    assignBottom();
    return true;
  };

  // A controller assignment preserves its intended follow state only at the exact assigned position;
  // any other position is the user's choice.
  const onScroll = (): void => {
    if (programmaticScroll) {
      const followsLatest = assignedFollowingLatest;
      programmaticScroll = false;
      if (body !== undefined && body.scrollTop === assignedTop) {
        setFollowingLatest(followsLatest);
        updateAgentTurnStartPosition();
        if (followsLatest && !isNearBottom()) {
          scrollToBottom();
        }
        return;
      }
    }
    setFollowingLatest(isNearBottom());
    updateAgentTurnStartPosition();
  };

  // This pane is shared presentation, so an exact owner/incarnation change starts with fresh follow state.
  createEffect(
    on(
      session,
      () => {
        programmaticScroll = false;
        setFollowingLatest(true);
        setAgentTurnStartAbove(false);
        scrollToBottom();
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
    const resizeObserver = new ResizeObserver(() => {
      if (followingLatest()) {
        scrollToBottom();
      } else {
        updateAgentTurnStartPosition();
      }
    });
    if (body !== undefined) {
      resizeObserver.observe(body);
      const transcript = body.querySelector<HTMLElement>("[data-agent-transcript]");
      if (transcript !== null) {
        resizeObserver.observe(transcript);
      }
    }
    const targetsPresentedSession = (target: ClientSession | null): boolean =>
      target !== null && target === session();
    const unregisterTurn = registerCommand(
      CommandIds.agentJumpToTurn,
      (_args, context) => targetsPresentedSession(context.session) && jumpToTurn(),
    );
    const unregisterLatest = registerCommand(
      CommandIds.agentJumpToLatest,
      (_args, context) => targetsPresentedSession(context.session) && jumpToLatest(),
    );
    onCleanup(() => {
      resizeObserver.disconnect();
      unregisterTurn();
      unregisterLatest();
    });
  });

  return {
    bindBody: (element: HTMLDivElement): void => {
      body = element;
    },
    followingLatest,
    followIfNearBottom: (): void => {
      if (isNearBottom()) {
        setFollowingLatest(true);
      }
    },
    jumpToLatest,
    jumpToTurn,
    onScroll,
    agentTurnStartAbove,
  };
}
