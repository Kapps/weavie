import { createMemo, createSignal, type JSX, onCleanup, onMount, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { NestedTranscript } from "./AgentNestedCard";
import { subagentStatus } from "./background-format";
import { liveNow } from "./live-clock";
import { agentPaneModel } from "./pane-store";
import { subagentForTab } from "./subagent-tab";

/** A subagent's whole read-only transcript as an editor tab, rendered exactly like the agent pane. */
export default function AgentSubagentTab(props: {
  session: ClientSession;
  path: string;
  bind(element: HTMLElement): () => void;
}): JSX.Element {
  let host!: HTMLDivElement;
  const entry = createMemo(() => subagentForTab(props.session, props.path));
  const now = liveNow(() => entry()?.subagent?.state === "running");
  const [expanded, setExpanded] = createSignal<ReadonlySet<string>>(new Set());
  onMount(() => onCleanup(props.bind(host)));

  return (
    <div class="editor-subagent agent-body" data-kind="editor" tabindex="0" ref={host}>
      <Show
        when={entry()}
        fallback={
          <Show when={agentPaneModel(props.session)?.historyComplete()}>
            <div class="editor-plan-notice">This subagent is no longer in the transcript.</div>
          </Show>
        }
      >
        {(subagent) => (
          <>
            <article class={`agent-entry agent-entry-subagent agent-tone-${subagent().tone}`}>
              <div class="agent-entry-head">
                <span class="agent-entry-label">Subagent</span>
                <small class="agent-entry-status">
                  {subagentStatus(subagent().subagent!, now())}
                </small>
              </div>
              <div class="agent-entry-main">
                <div class="agent-entry-summary">{subagent().subagent!.name}</div>
                <Show when={subagent().subagent!.task}>
                  {(task) => <div class="agent-entry-summary">{task()}</div>}
                </Show>
              </div>
            </article>
            <NestedTranscript
              entries={subagent().asideEntries ?? []}
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
          </>
        )}
      </Show>
    </div>
  );
}
