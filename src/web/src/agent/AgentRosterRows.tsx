import { CircleStop } from "lucide-solid";
import { type JSX, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { liveKeyHint } from "../commands/keys-live";
import { runCommandWithFeedback } from "../commands/registry";
import { CommandIds } from "../commands/types";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { type AgentBackgroundItem, backgroundRunning } from "./agent-background-store";
import { cardOf, openCard, openCardTitle, ownCardOnFocus } from "./agent-cards";
import { backgroundElapsed } from "./background-format";

/** A row's state and elapsed time, each in its own column of the roster. */
function RosterStatus(props: {
  state: string;
  startedAtMs: number | null;
  endedAtMs: number | null;
  now: number;
}): JSX.Element {
  return (
    <>
      <small class="agent-entry-status">{props.state}</small>
      <small class="agent-entry-status agent-roster-time">
        {backgroundElapsed(props.state, props.startedAtMs, props.endedAtMs, props.now)}
      </small>
    </>
  );
}

/** A running row's Stop: greyed out with the reason while its work can't be stopped, and an empty slot once it ends. */
function RosterStop(props: {
  name: string;
  running: boolean;
  stopId: string | null;
  blocked: string;
}): JSX.Element {
  return (
    <Show
      when={props.running}
      fallback={<span class="ui-icon-button agent-roster-stop-none" aria-hidden="true" />}
    >
      <button
        type="button"
        class="ui-icon-button agent-roster-stop"
        aria-label={`Stop ${props.name}`}
        aria-disabled={props.stopId === null}
        title={
          props.stopId === null
            ? props.blocked
            : `Stop Background Task${liveKeyHint(CommandIds.stopBackgroundTask)}`
        }
        onClick={() =>
          props.stopId !== null &&
          void runCommandWithFeedback(CommandIds.stopBackgroundTask, { id: props.stopId })
        }
      >
        <CircleStop aria-hidden="true" />
      </button>
    </Show>
  );
}

export const subagentRunning = (entry: AgentTranscriptEntry): boolean =>
  entry.subagent?.state === "running";

/** A subagent's row, which opens its transcript, above the requests it is waiting on. */
export function SubagentRow(props: {
  entry: AgentTranscriptEntry;
  now: number;
  session: ClientSession;
  pending: JSX.Element;
}): JSX.Element {
  const info = () => props.entry.subagent!;
  const own = ownCardOnFocus(() => ({
    session: props.session,
    card: cardOf(props.entry)!,
    toggle: null,
  }));
  return (
    <article
      data-agent-subagent={props.entry.conversationId}
      data-state={info().state}
      onFocusIn={own}
      onPointerDown={own}
    >
      <div class="agent-roster-row">
        <button
          type="button"
          class="ui-row agent-subagent-select"
          aria-label={info().name}
          title={openCardTitle()}
          onClick={() => void openCard(cardOf(props.entry)!)}
        >
          <span class="agent-subagent-name">
            {info().name}
            <Show when={info().via}>
              {(via) => <span class="agent-roster-via"> · via {via()}</span>}
            </Show>
          </span>
          <RosterStatus
            state={info().state}
            startedAtMs={info().startedAtMs}
            endedAtMs={info().completedAtMs}
            now={props.now}
          />
        </button>
        <RosterStop
          name={info().name}
          running={subagentRunning(props.entry)}
          stopId={null}
          blocked="A subagent can't be stopped on its own"
        />
      </div>
      <Show when={(props.entry.pendingEntries?.length ?? 0) > 0}>{props.pending}</Show>
    </article>
  );
}

/** A background task's row, which opens its tab. */
export function TaskRow(props: { item: AgentBackgroundItem; now: number }): JSX.Element {
  return (
    <div
      class="agent-roster-row"
      data-background-item={props.item.id}
      data-state={props.item.state}
    >
      <button
        type="button"
        class="ui-row agent-subagent-select"
        title="Open Background Work"
        onClick={() =>
          void runCommandWithFeedback(CommandIds.openBackgroundItem, { id: props.item.id })
        }
      >
        <span class="agent-subagent-name">{props.item.name}</span>
        <RosterStatus
          state={props.item.state}
          startedAtMs={props.item.startedAtMs}
          endedAtMs={props.item.endedAtMs}
          now={props.now}
        />
      </button>
      <RosterStop
        name={props.item.name}
        running={backgroundRunning(props.item)}
        stopId={props.item.canStop ? props.item.id : null}
        blocked="This task can't be stopped on its own"
      />
    </div>
  );
}
