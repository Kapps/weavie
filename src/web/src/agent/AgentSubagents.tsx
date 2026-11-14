import { CircleStop } from "lucide-solid";
import {
  createEffect,
  createMemo,
  createSignal,
  For,
  type JSX,
  on,
  onCleanup,
  Show,
} from "solid-js";
import { setContext } from "../commands/context";
import { liveKeyHint } from "../commands/keys-live";
import { registerCommand, runCommandWithFeedback } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { DisclosureIcon } from "./AgentDisclosure";
import { NestedTranscript } from "./AgentNestedCard";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { agentBackground, backgroundItem, backgroundRunning } from "./agent-background-store";
import { cardOf, openCard, openCardTitle, ownCardOnFocus } from "./agent-cards";
import { backgroundElapsed } from "./background-format";
import { liveNow } from "./live-clock";
import type { AgentPaneModel } from "./pane-store";
import "./agent-subagents.css";

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

const runningFirst = <T,>(items: readonly T[], running: (item: T) => boolean): T[] =>
  [...items].sort((a, b) => Number(running(b)) - Number(running(a)));

/**
 * The session's subagents and background tasks, collapsed by default; pending requests remain visible when it
 * is collapsed.
 */
export function AgentSubagents(props: { model: AgentPaneModel }): JSX.Element {
  const [expanded, setExpanded] = createSignal(false);
  const [details, setDetails] = createSignal<ReadonlySet<string>>(new Set());
  let toggle: HTMLButtonElement | undefined;
  const subagentRunning = (entry: AgentTranscriptEntry) => entry.subagent?.state === "running";
  const entries = createMemo(() => runningFirst(props.model.subagents(), subagentRunning));
  const tasks = createMemo(() =>
    runningFirst(
      agentBackground(props.model.session).filter((item) => item.kind !== "subagent"),
      backgroundRunning,
    ),
  );
  // Ids, not items: the host replaces every item per update, and a row must keep its focus across one.
  const taskIds = createMemo(() => tasks().map((item) => item.id));
  const count = () => entries().length + tasks().length;
  const running = () =>
    entries().filter(subagentRunning).length + tasks().filter(backgroundRunning).length;
  const now = liveNow(() => running() > 0);
  const visible = () =>
    entries().filter((entry) => expanded() || (entry.pendingEntries?.length ?? 0) > 0);
  const title = () => `Expand or Collapse Subagents${liveKeyHint(CommandIds.toggleSubagents)}`;
  createEffect(
    on(props.model.generation, () => {
      setExpanded(false);
      setDetails(new Set<string>());
    }),
  );
  createEffect(() => setContext("agentSubagentsAvailable", count() > 0));
  onCleanup(() => setContext("agentSubagentsAvailable", false));
  onCleanup(
    registerCommand(CommandIds.toggleSubagents, (_args, context) => {
      if (context.session !== props.model.session || count() === 0) return false;
      setExpanded((value) => !value);
      if (expanded()) toggle?.focus({ preventScroll: true });
      return true;
    }),
  );

  return (
    <Show when={count() > 0}>
      <section class="agent-subagents" data-agent-subagents="" aria-label="Subagents">
        <button
          ref={toggle}
          type="button"
          class="ui-row agent-subagents-toggle"
          aria-expanded={expanded()}
          aria-label={`${expanded() ? "Collapse" : "Expand"} Subagents`}
          title={title()}
          onClick={() => void runCommandWithFeedback(CommandIds.toggleSubagents)}
        >
          <DisclosureIcon />
          <span>Subagents</span>
          <span>{count()}</span>
          <Show when={running() > 0}>
            <span class="ui-icon">
              <span class="agent-working-spinner" aria-hidden="true" />
            </span>
            <span>{running()} running</span>
          </Show>
        </button>
        <Show when={expanded() || visible().length > 0}>
          <div class="agent-subagents-list">
            <For each={visible()}>
              {(entry) => {
                const own = ownCardOnFocus(() => ({
                  session: props.model.session,
                  card: cardOf(entry)!,
                  toggle: null,
                }));
                return (
                  <article
                    data-agent-subagent={entry.conversationId}
                    data-state={entry.subagent!.state}
                    onFocusIn={own}
                    onPointerDown={own}
                  >
                    <div class="agent-roster-row">
                      <button
                        type="button"
                        class="ui-row agent-subagent-select"
                        aria-label={entry.subagent!.name}
                        title={openCardTitle()}
                        onClick={() => void openCard(cardOf(entry)!)}
                      >
                        <span class="agent-subagent-name">
                          {entry.subagent!.name}
                          <Show when={entry.subagent!.via}>
                            {(via) => <span class="agent-roster-via"> · via {via()}</span>}
                          </Show>
                        </span>
                        <RosterStatus
                          state={entry.subagent!.state}
                          startedAtMs={entry.subagent!.startedAtMs}
                          endedAtMs={entry.subagent!.completedAtMs}
                          now={now()}
                        />
                      </button>
                      <RosterStop
                        name={entry.subagent!.name}
                        running={subagentRunning(entry)}
                        stopId={null}
                        blocked="A subagent can't be stopped on its own"
                      />
                    </div>
                    <Show when={(entry.pendingEntries?.length ?? 0) > 0}>
                      <NestedTranscript
                        currentThought={null}
                        entries={entry.pendingEntries!}
                        pendingOnly={true}
                        expandedDetails={details()}
                        keyboardRequestKey={props.model.keyboardRequestKey()}
                        onDetailsToggle={(id, open) => {
                          const next = new Set(details());
                          if (open) next.add(id);
                          else next.delete(id);
                          setDetails(next);
                        }}
                        onUpdatesToggle={(id, open) => props.model.setActivityExpanded(id, open)}
                        session={props.model.session}
                      />
                    </Show>
                  </article>
                );
              }}
            </For>
            <Show when={expanded()}>
              <For each={taskIds()}>
                {(id) => (
                  <Show when={backgroundItem(props.model.session, id)}>
                    {(item) => (
                      <div
                        class="agent-roster-row"
                        data-background-item={id}
                        data-state={item().state}
                      >
                        <button
                          type="button"
                          class="ui-row agent-subagent-select"
                          title="Open Background Work"
                          onClick={() =>
                            void runCommandWithFeedback(CommandIds.openBackgroundItem, { id })
                          }
                        >
                          <span class="agent-subagent-name">{item().name}</span>
                          <RosterStatus
                            state={item().state}
                            startedAtMs={item().startedAtMs}
                            endedAtMs={item().endedAtMs}
                            now={now()}
                          />
                        </button>
                        <RosterStop
                          name={item().name}
                          running={backgroundRunning(item())}
                          stopId={item().canStop ? id : null}
                          blocked="This task can't be stopped on its own"
                        />
                      </div>
                    )}
                  </Show>
                )}
              </For>
            </Show>
          </div>
        </Show>
      </section>
    </Show>
  );
}
