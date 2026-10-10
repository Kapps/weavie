import { createSignal, type JSX, onMount, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { liveKeyHint } from "../commands/keys-live";
import { CommandIds } from "../commands/types";
import { NestedTranscript, registerNestedToggle } from "./AgentNestedCard";
import { subagentCardEntries } from "./AgentPaneSideConversations";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { AgentSubagentReader } from "./AgentSubagentReader";
import { backgroundStatus } from "./background-format";
import { liveNow } from "./live-clock";

/** A read-only subagent row: expanded while it runs, collapsed once it finishes, with Open for its full transcript. */
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
  const collapsed = () => running() === flipped();
  const toggle = () => props.onDetailsToggle(props.entry.id, !flipped());
  const [reading, setReading] = createSignal(false);
  const now = liveNow(running);
  const status = () => {
    const base = backgroundStatus(
      info().state,
      info().startedAtMs,
      info().completedAtMs,
      now(),
      null,
    );
    return info().via === null ? base : `${base} · via ${info().via}`;
  };
  const toggleTitle = () =>
    `${collapsed() ? "Expand" : "Collapse"} subagent${liveKeyHint(CommandIds.toggleAgentAside)}`;
  let card: HTMLElement | undefined;
  onMount(() => registerNestedToggle(card!, toggle));

  return (
    <article
      ref={card}
      class={`agent-entry agent-entry-subagent agent-tone-${props.entry.tone}`}
      data-agent-subagent={props.entry.conversationId}
      data-state={info().state}
    >
      <div class="agent-entry-head">
        <button
          type="button"
          class="agent-entry-toggle"
          aria-expanded={!collapsed()}
          aria-label={toggleTitle()}
          title={toggleTitle()}
          onClick={toggle}
        >
          <span aria-hidden="true">{collapsed() ? "▸" : "▾"}</span>
          <span class="agent-entry-label">Subagent</span>
        </button>
        <Show when={running()}>
          <span class="agent-working-spinner" aria-hidden="true" />
        </Show>
        <small class="agent-entry-status">{status()}</small>
        <button
          type="button"
          class="agent-entry-rewind agent-entry-open"
          onClick={() => setReading(true)}
        >
          Open
        </button>
      </div>
      <div class="agent-entry-main">
        <div class="agent-entry-summary" title={info().task ?? undefined}>
          {info().name}
        </div>
        <Show when={!collapsed()}>
          <NestedTranscript
            entries={subagentCardEntries(props.entry.asideEntries ?? [])}
            expandedDetails={props.expandedDetails}
            keyboardRequestKey={props.keyboardRequestKey}
            onDetailsToggle={props.onDetailsToggle}
            session={props.session}
          />
        </Show>
      </div>
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
    </article>
  );
}
