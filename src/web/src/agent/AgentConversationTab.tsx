import { createMemo, createSignal, type JSX, onCleanup, onMount, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { AgentAsideReplyBox } from "./AgentAsideReplyBox";
import { NestedTranscript } from "./AgentNestedCard";
import { SubagentStatus } from "./AgentSubagentEntry";
import { cardForTabEntry } from "./card-tab";
import { liveNow } from "./live-clock";
import { agentPaneModel } from "./pane-store";

/** A subagent's or BTW's whole transcript as an editor tab, rendered like the agent pane; a BTW keeps its reply box. */
export default function AgentConversationTab(props: {
  session: ClientSession;
  kind: "subagent" | "aside";
  path: string;
  bind(element: HTMLElement): () => void;
}): JSX.Element {
  let host!: HTMLDivElement;
  const entry = createMemo(() => cardForTabEntry(props.session, props.kind, props.path));
  const now = liveNow(() => entry()?.subagent?.state === "running");
  const [expanded, setExpanded] = createSignal<ReadonlySet<string>>(new Set());
  onMount(() => onCleanup(props.bind(host)));

  return (
    <div class="editor-agent-view agent-body" data-kind="editor" tabindex="0" ref={host}>
      <Show
        when={entry()}
        fallback={
          <Show when={agentPaneModel(props.session)?.historyComplete()}>
            <div class="editor-plan-notice">This conversation is no longer in the transcript.</div>
          </Show>
        }
      >
        {(card) => (
          <>
            <Show when={card().subagent}>
              {(subagent) => (
                <article class={`agent-entry agent-entry-subagent agent-tone-${card().tone}`}>
                  <div class="agent-entry-head">
                    <span class="agent-entry-label">Subagent</span>
                    <SubagentStatus info={subagent()} now={now()} />
                  </div>
                  <div class="agent-entry-main">
                    <div class="agent-entry-summary">{subagent().name}</div>
                    <Show when={subagent().task}>
                      {(task) => <div class="agent-entry-detail">{task()}</div>}
                    </Show>
                  </div>
                </article>
              )}
            </Show>
            <NestedTranscript
              currentThought={card().currentThought ?? null}
              pendingOnly={false}
              entries={card().asideEntries ?? []}
              expandedDetails={expanded()}
              keyboardRequestKey={null}
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
      </Show>
    </div>
  );
}
