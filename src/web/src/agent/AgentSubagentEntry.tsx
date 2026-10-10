import { createSignal, type JSX, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { AgentNestedCard } from "./AgentNestedCard";
import { subagentCardEntries } from "./AgentPaneSideConversations";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { AgentSubagentReader } from "./AgentSubagentReader";
import { liveNow } from "./live-clock";
import { formatElapsed } from "./turn-progress";

/** A read-only subagent: expanded while it runs, collapsed once it finishes, with Open for its full transcript. */
export function AgentSubagentEntry(props: {
  entry: AgentTranscriptEntry;
  expandedDetails: ReadonlySet<string>;
  onDetailsToggle: (entryId: string, open: boolean) => void;
  keyboardRequestKey: string | null;
  session: ClientSession;
}): JSX.Element {
  const info = () => props.entry.subagent!;
  const running = () => info().state === "running";
  const flipped = () => props.expandedDetails.has(props.entry.id);
  const [reading, setReading] = createSignal(false);
  const now = liveNow(running);
  const elapsed = () => {
    const started = info().startedAtMs;
    return started === null ? null : formatElapsed((info().completedAtMs ?? now()) - started);
  };

  return (
    <>
      <AgentNestedCard
        attributes={{
          "data-agent-subagent": props.entry.conversationId!,
          "data-state": info().state,
        }}
        collapsed={running() === flipped()}
        entries={subagentCardEntries(props.entry.asideEntries ?? [])}
        expandedDetails={props.expandedDetails}
        head={
          <>
            <span class="agent-nested-badge">Subagent</span>
            <span class="agent-nested-name" title={info().task ?? undefined}>
              {info().name}
            </span>
            <Show when={running()}>
              <span class="agent-working-spinner" aria-hidden="true" />
            </Show>
            <small>
              {[info().state, elapsed(), info().via === null ? null : `via ${info().via}`]
                .filter((part) => part !== null)
                .join(" · ")}
            </small>
          </>
        }
        headActions={
          <button type="button" class="agent-nested-action" onClick={() => setReading(true)}>
            Open
          </button>
        }
        keyboardRequestKey={props.keyboardRequestKey}
        label="subagent"
        onDetailsToggle={props.onDetailsToggle}
        onToggle={() => props.onDetailsToggle(props.entry.id, !flipped())}
        session={props.session}
      >
        <Show when={info().task}>{(task) => <div class="agent-nested-task">{task()}</div>}</Show>
      </AgentNestedCard>
      <Show when={reading()}>
        <AgentSubagentReader
          entry={props.entry}
          expandedDetails={props.expandedDetails}
          keyboardRequestKey={props.keyboardRequestKey}
          onClose={() => setReading(false)}
          onDetailsToggle={props.onDetailsToggle}
          session={props.session}
        />
      </Show>
    </>
  );
}
