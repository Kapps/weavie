import type { ScrollEvent } from "@codingame/monaco-vscode-api/vscode/vs/base/common/scrollable";
import { type Accessor, createEffect, createSignal, onCleanup, onMount } from "solid-js";
import type { ClientSession } from "../bridge";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import type { TranscriptViewport } from "./AgentTranscriptViewport";

export function createAgentPaneScroll(
  session: ClientSession,
  viewport: Accessor<TranscriptViewport | undefined>,
  lineHeight: Accessor<number>,
  turnStartIndex: Accessor<number | null>,
  turnNavigable: Accessor<boolean>,
  initiallyFollowingLatest: boolean,
) {
  const [followingLatest, setFollowingLatest] = createSignal(initiallyFollowingLatest);
  const [agentTurnStartAbove, setAgentTurnStartAbove] = createSignal(false);
  let navigating = false;
  const nearBottom = (): boolean => {
    const view = viewport();
    return (
      view !== undefined && view.contentHeight() - view.height() - view.offset() <= lineHeight() * 3
    );
  };
  const updateTurnPosition = (): void => {
    const view = viewport();
    const index = turnStartIndex();
    setAgentTurnStartAbove(
      turnNavigable() &&
        view !== undefined &&
        index !== null &&
        view.itemTop(index) < view.offset(),
    );
  };
  const navigate = (offset: number, follow: boolean): boolean => {
    const view = viewport();
    if (view === undefined) return false;
    const previous = view.offset();
    setFollowingLatest(follow);
    navigating = true;
    try {
      view.jumpTo(offset);
    } finally {
      navigating = false;
    }
    updateTurnPosition();
    return view.offset() !== previous;
  };
  const jumpToLatest = (): boolean => {
    const view = viewport();
    return view !== undefined && navigate(view.contentHeight(), true);
  };
  const jumpToTurn = (): boolean => {
    const view = viewport();
    const index = turnStartIndex();
    return (
      turnNavigable() &&
      view !== undefined &&
      index !== null &&
      navigate(view.itemTop(index), false)
    );
  };
  createEffect(updateTurnPosition);
  onMount(() => {
    const unregisterTurn = registerCommand(
      CommandIds.agentJumpToTurn,
      (_args, context) => context.session === session && jumpToTurn(),
    );
    const unregisterLatest = registerCommand(
      CommandIds.agentJumpToLatest,
      (_args, context) => context.session === session && jumpToLatest(),
    );
    onCleanup(() => {
      unregisterTurn();
      unregisterLatest();
    });
  });
  return {
    followingLatest,
    agentTurnStartAbove,
    jumpToLatest,
    jumpToTurn,
    followIfNearBottom: (): void => {
      if (nearBottom()) setFollowingLatest(true);
    },
    onWillScroll: (event: ScrollEvent): void => {
      if (event.source === "position" && !navigating) setFollowingLatest(false);
    },
    onDidScroll: (event: ScrollEvent): void => {
      if (event.source === "position" && !navigating && !event.inSmoothScrolling)
        setFollowingLatest(nearBottom());
      updateTurnPosition();
    },
  };
}
