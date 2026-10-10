import { createMemo, createSignal, type JSX, onCleanup, onMount, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { NestedTranscript } from "./AgentNestedCard";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { subagentStatus } from "./background-format";
import { liveNow } from "./live-clock";
import { agentPaneModel } from "./pane-store";

const prefix = "subagent:";

/** The editor tab path of one subagent's transcript. */
export function subagentTabPath(conversationId: string): string {
  return prefix + conversationId;
}

/** The live subagent entry a subagent tab shows, from its session's own transcript. */
export function subagentEntry(
  session: ClientSession | null,
  path: string,
): AgentTranscriptEntry | undefined {
  const conversationId = path.slice(prefix.length);
  return agentPaneModel(session)?.entries.find(
    (entry) => entry.kind === "subagent" && entry.conversationId === conversationId,
  );
}

/** A subagent's whole read-only transcript as an editor tab, rendered exactly like the agent pane. */
export default function AgentSubagentTab(props: {
  session: ClientSession;
  path: string;
  bind(element: HTMLElement): () => void;
}): JSX.Element {
  let host!: HTMLDivElement;
  const entry = createMemo(() => subagentEntry(props.session, props.path));
  const now = liveNow(() => entry()?.subagent?.state === "running");
  const [expanded, setExpanded] = createSignal<ReadonlySet<string>>(new Set());
  onMount(() => onCleanup(props.bind(host)));

  return (
    <div class="editor-subagent agent-body" data-kind="editor" tabindex="0" ref={host}>
      <Show
        when={entry()}
        fallback={
          <div class="editor-plan-notice">This subagent is no longer in the transcript.</div>
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
