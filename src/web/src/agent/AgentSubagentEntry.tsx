import { createMemo, type JSX, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { liveKeyHint } from "../commands/keys-live";
import { CommandIds } from "../commands/types";
import { AgentCardOpenButton } from "./AgentCardOpenButton";
import { NestedTranscript } from "./AgentNestedCard";
import { subagentCardEntries } from "./AgentPaneSideConversations";
import type { AgentSubagentInfo, AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { ownCardOnFocus } from "./agent-cards";
import { backgroundStatus } from "./background-format";
import { liveNow } from "./live-clock";

/** A collapsed-by-default subagent row with visible requests and Open for its full transcript. */
export function AgentSubagentEntry(props: {
  entry: AgentTranscriptEntry;
  expandedDetails: ReadonlySet<string>;
  onDetailsToggle: (entryId: string, open: boolean) => void;
  keyboardRequestKey: string | null;
  session: ClientSession;
}): JSX.Element {
  const info = () => props.entry.subagent!;
  const running = () => info().state === "running";
  const collapsed = () => !props.expandedDetails.has(props.entry.id);
  const toggle = () => props.onDetailsToggle(props.entry.id, collapsed());
  const entries = createMemo(() => subagentCardEntries(props.entry.asideEntries ?? [], true));
  const now = liveNow(running);
  const toggleTitle = () =>
    `${collapsed() ? "Expand" : "Collapse"} subagent${liveKeyHint(CommandIds.toggleAgentAside)}`;
  const card = { kind: "subagent", conversationId: props.entry.conversationId! } as const;
  const own = ownCardOnFocus(() => ({ session: props.session, card, toggle }));

  return (
    <article
      class={`agent-entry agent-entry-subagent agent-tone-${props.entry.tone}`}
      data-agent-subagent={props.entry.conversationId}
      data-state={info().state}
      onFocusIn={own}
      onPointerDown={own}
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
        <SubagentStatus info={info()} now={now()} />
        <AgentCardOpenButton card={card} />
      </div>
      <div class="agent-entry-main">
        <div class="agent-entry-summary" title={info().task ?? undefined}>
          {info().name}
        </div>
        <Show when={entries().length > 0 || props.entry.currentThought}>
          <NestedTranscript
            currentThought={props.entry.currentThought ?? null}
            entries={entries()}
            pendingOnly={collapsed()}
            expandedDetails={props.expandedDetails}
            keyboardRequestKey={props.keyboardRequestKey}
            onDetailsToggle={props.onDetailsToggle}
            session={props.session}
          />
        </Show>
      </div>
    </article>
  );
}

/** A subagent's state and elapsed time, and the parent a nested spawn came from. */
export function SubagentStatus(props: { info: AgentSubagentInfo; now: number }): JSX.Element {
  return (
    <small class="agent-entry-status">
      {backgroundStatus(
        props.info.state,
        props.info.startedAtMs,
        props.info.completedAtMs,
        props.now,
        null,
      )}
      <Show when={props.info.via}>
        {(via) => <span class="agent-entry-via"> · via {via()}</span>}
      </Show>
    </small>
  );
}
