import { type JSX, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { keyHint } from "../commands/key-hint";
import { CommandIds } from "../commands/types";
import { AgentCardReplyBox } from "./AgentCardReplyBox";
import { AgentCardTabFrame } from "./AgentCardTabFrame";
import { NestedTranscript } from "./AgentNestedCard";
import type { AgentSubagentInfo, AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { replyComposer } from "./composer-store";
import { liveNow } from "./live-clock";
import { createNestedExpansion } from "./nested-expansion";
import { SubagentStatus } from "./SubagentStatus";

/** A subagent's or BTW's whole transcript as an editor tab, rendered like the agent pane; a BTW keeps its reply box. */
export default function AgentConversationTab(props: {
  session: ClientSession;
  kind: "subagent" | "aside";
  path: string;
  bind(element: HTMLElement): () => void;
}): JSX.Element {
  const expansion = createNestedExpansion(props.session);
  return (
    <AgentCardTabFrame
      session={props.session}
      kind={props.kind}
      path={props.path}
      noun="conversation"
      class=""
      bind={props.bind}
    >
      {(card) => (
        <>
          <Show when={card().subagent}>
            {(subagent) => <SubagentHeader card={card()} subagent={subagent()} />}
          </Show>
          <NestedTranscript
            pendingOnly={false}
            entries={card().asideEntries ?? []}
            expandedDetails={expansion.expandedDetails()}
            keyboardRequestKey={null}
            onUpdatesToggle={expansion.onUpdatesToggle}
            onDetailsToggle={expansion.onDetailsToggle}
            session={props.session}
          />
          <Show when={props.kind === "aside" && card().asideReplyable !== false}>
            <AgentCardReplyBox
              session={props.session}
              composer={replyComposer(props.session, card().conversationId!)}
              label="Reply to BTW"
              hint={`Paste text or images${keyHint(CommandIds.agentPaste)}`}
              disabled={card().asideActive === true}
            />
          </Show>
        </>
      )}
    </AgentCardTabFrame>
  );
}

function SubagentHeader(props: {
  card: AgentTranscriptEntry;
  subagent: AgentSubagentInfo;
}): JSX.Element {
  const now = liveNow(() => props.subagent.state === "running");
  return (
    <article class={`agent-entry agent-entry-subagent agent-tone-${props.card.tone}`}>
      <div class="agent-entry-head">
        <span class="agent-entry-label">Subagent</span>
        <SubagentStatus info={props.subagent} now={now()} />
      </div>
      <div class="agent-entry-main">
        <div class="agent-entry-summary">{props.subagent.name}</div>
        <Show when={props.subagent.task}>
          {(task) => <div class="agent-entry-detail">{task()}</div>}
        </Show>
      </div>
    </article>
  );
}
