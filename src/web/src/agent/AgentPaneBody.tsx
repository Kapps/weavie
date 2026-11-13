import { createEffect, createMemo, type JSX, on, onCleanup } from "solid-js";
import { setContext } from "../commands/context";

import { AgentComposer } from "./AgentComposer";
import { AgentCurrentThought } from "./AgentCurrentThought";
import { AgentStrip } from "./AgentStrip";
import { AgentSubagents } from "./AgentSubagents";
import { AgentTranscriptViewport } from "./AgentTranscriptViewport";
import { agentBackground, backgroundRunning } from "./agent-background-store";
import { agentControlState } from "./agent-controls-store";
import type { AgentPaneModel } from "./pane-store";

export function AgentPaneBody(props: {
  active: boolean;
  compact: boolean;
  inputProtocol: number;
  model: AgentPaneModel;
  providerName: string;
}): JSX.Element {
  createEffect(() =>
    setContext("agentRewindable", agentControlState(props.model.session).rewindable),
  );
  onCleanup(() => setContext("agentRewindable", false));
  createEffect(() =>
    setContext(
      "agentBackgroundActive",
      agentBackground(props.model.session).some(backgroundRunning),
    ),
  );
  onCleanup(() => setContext("agentBackgroundActive", false));
  createEffect(() =>
    setContext(
      "agentStripActive",
      agentBackground(props.model.session).some((item) => item.kind !== "subagent") ||
        props.model.cards().length > 0,
    ),
  );
  onCleanup(() => setContext("agentStripActive", false));
  let jumpToLatest = (): boolean => false;
  // Each transcript generation gets its own viewport, so /clear and Rewind carry no reading state across.
  const viewport = createMemo(
    on(props.model.generation, () => (
      <AgentTranscriptViewport
        compact={props.compact}
        model={props.model}
        onJumpToLatest={(jump) => {
          jumpToLatest = jump;
        }}
        providerName={props.providerName}
      />
    )),
  );
  return (
    <>
      {viewport()}
      <AgentSubagents model={props.model} />
      <AgentStrip session={props.model.session} cards={props.model.cards()} />
      <AgentCurrentThought text={props.model.transcript().currentThought} />
      <AgentComposer
        active={props.active}
        compact={props.compact}
        history={props.model.history()}
        inputProtocol={props.inputProtocol}
        interruptible={props.model.interruptible()}
        latestPlan={props.model.latestPlan()}
        pendingApprovalId={props.model.keyboardApprovalId()}
        pendingKind={props.model.pendingRequestKind()}
        pendingLegacyImageCount={props.model.pendingLegacyImageCount()}
        session={props.model.session}
        turnActive={props.model.turnActive()}
        turnStartedAt={props.model.turnStartedAt()}
        onSubmitted={() => jumpToLatest()}
      />
    </>
  );
}
