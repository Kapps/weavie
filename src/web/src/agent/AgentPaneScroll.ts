import { type Accessor, createEffect, createSignal, on, onCleanup, onMount } from "solid-js";
import type { AgentPaneUpdate, ClientSession } from "../bridge";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";

export function createAgentPaneScroll(
  session: Accessor<ClientSession | null>,
  messages: Accessor<readonly AgentPaneUpdate[]>,
) {
  let body: HTMLDivElement | undefined;
  let scrollScheduled = false;
  let programmaticScroll = false;
  let assignedTop = 0;
  const [followingLatest, setFollowingLatest] = createSignal(true);
  const [turnStartAbove, setTurnStartAbove] = createSignal(false);

  const isNearBottom = (): boolean => {
    if (body === undefined) {
      return true;
    }
    const distance = body.scrollHeight - body.scrollTop - body.clientHeight;
    const lineHeight = Number.parseFloat(getComputedStyle(body).lineHeight);
    return distance <= Math.ceil(lineHeight * 3);
  };

  const turnStart = (): HTMLElement | null =>
    body?.querySelector<HTMLElement>("[data-agent-turn-start]") ?? null;

  const updateTurnStartPosition = (): void => {
    const start = turnStart();
    setTurnStartAbove(
      body !== undefined &&
        start !== null &&
        start.getBoundingClientRect().top < body.getBoundingClientRect().top,
    );
  };

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
      const previous = body.scrollTop;
      body.scrollTop = body.scrollHeight;
      assignedTop = body.scrollTop;
      programmaticScroll = assignedTop !== previous;
      updateTurnStartPosition();
    });
  };

  const jumpToTurn = (): boolean => {
    const start = turnStart();
    if (body === undefined || start === null) {
      return false;
    }
    const top =
      body.scrollTop + start.getBoundingClientRect().top - body.getBoundingClientRect().top;
    if (Math.abs(body.scrollTop - top) < 1) {
      return false;
    }
    setFollowingLatest(false);
    body.scrollTop = top;
    updateTurnStartPosition();
    return true;
  };

  const jumpToLatest = (): boolean => {
    if (body === undefined || (followingLatest() && isNearBottom())) {
      return false;
    }
    setFollowingLatest(true);
    scrollToBottom();
    return true;
  };

  // A scheduled bottom assignment may share a scroll event with user input; only the exact assigned
  // position keeps following, while any other position is treated as the user's choice.
  const onScroll = (): void => {
    if (programmaticScroll) {
      programmaticScroll = false;
      if (body !== undefined && body.scrollTop === assignedTop) {
        updateTurnStartPosition();
        if (!isNearBottom()) {
          scrollToBottom();
        }
        return;
      }
    }
    setFollowingLatest(isNearBottom());
    updateTurnStartPosition();
  };

  createEffect(
    on(messages, () => {
      if (followingLatest()) {
        scrollToBottom();
      }
    }),
  );

  // This pane is shared presentation, so an exact owner/incarnation change starts with fresh follow state.
  createEffect(
    on(
      session,
      () => {
        programmaticScroll = false;
        setFollowingLatest(true);
        setTurnStartAbove(false);
        scrollToBottom();
      },
      { defer: true },
    ),
  );

  onMount(() => {
    const resizeObserver = new ResizeObserver(() => {
      if (followingLatest()) {
        scrollToBottom();
      } else {
        updateTurnStartPosition();
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
    turnStartAbove,
  };
}
