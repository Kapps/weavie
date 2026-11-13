import { createSignal, type JSX, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { AgentAsideReplyBox } from "./AgentAsideReplyBox";
import { AgentCardTabFrame } from "./AgentCardTabFrame";
import { NestedTranscript } from "./AgentNestedCard";
import type { AgentSubagentInfo, AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { SubagentStatus } from "./AgentSubagentEntry";
import { liveNow } from "./live-clock";
import { agentPaneModel } from "./pane-store";

/** A subagent's or BTW's whole transcript as an editor tab, rendered like the agent pane; a BTW keeps its reply box. */
export default function AgentConversationTab(props: {
  session: ClientSession;
  kind: "subagent" | "aside";
  path: string;
  bind(element: HTMLElement): () => void;
}): JSX.Element {
  const [expanded, setExpanded] = createSignal<ReadonlySet<string>>(new Set());
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
            currentThought={card().currentThought ?? null}
            pendingOnly={false}
            entries={card().asideEntries ?? []}
            expandedDetails={expanded()}
            keyboardRequestKey={null}
            onUpdatesToggle={(id, open) =>
              agentPaneModel(props.session)!.setActivityExpanded(id, open)
            }
            onDetailsToggle={(id, open) => {
              const next = new Set(expanded());
              if (open) next.add(id);
              else next.delete(id);
              setExpanded(next);
            }}
            session={props.session}
          />
          <Show when={props.kind === "aside" && card().asideReplyable !== false}>
            <AgentAsideReplyBox
              session={props.session}
              conversationId={card().conversationId!}
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
