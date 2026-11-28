import {
  createEffect,
  createMemo,
  createSignal,
  For,
  type JSX,
  Match,
  on,
  Show,
  Switch,
} from "solid-js";
import { CommandIds } from "../commands/types";
import { NestedTranscript } from "./AgentNestedCard";
import { createRoster } from "./AgentRoster";
import { SubagentRow, subagentRunning, TaskRow } from "./AgentRosterRows";
import { agentBackground, backgroundRunning } from "./agent-background-store";
import { liveNow } from "./live-clock";
import type { AgentPaneModel } from "./pane-store";
import "./agent-subagents.css";

/**
 * The session's subagents and background tasks, collapsed by default; pending requests remain visible when it
 * is collapsed.
 */
export function AgentSubagents(props: { model: AgentPaneModel }): JSX.Element {
  const [details, setDetails] = createSignal<ReadonlySet<string>>(new Set());
  const subagents = createMemo(
    () =>
      new Map(props.model.subagents().map((entry) => [`subagent:${entry.conversationId}`, entry])),
  );
  const tasks = createMemo(
    () =>
      new Map(
        agentBackground(props.model.session)
          .filter((item) => item.kind !== "subagent")
          .map((item) => [`task:${item.id}`, item]),
      ),
  );
  // One list, running rows first; a stable sort keeps subagents ahead of tasks within each half.
  const rows = createMemo(() =>
    [
      ...[...subagents()].map(([key, entry]) => ({
        key,
        running: subagentRunning(entry),
        pending: (entry.pendingEntries?.length ?? 0) > 0,
      })),
      ...[...tasks()].map(([key, item]) => ({
        key,
        running: backgroundRunning(item),
        pending: false,
      })),
    ].sort((a, b) => Number(b.running) - Number(a.running)),
  );
  const count = () => rows().length;
  const roster = createRoster({
    model: props.model,
    label: "Subagents",
    command: CommandIds.toggleSubagents,
    available: "agentSubagentsAvailable",
    count,
    startsExpanded: false,
  });
  // Keys, not items: every update replaces the items, and a row must keep its focus across one.
  const visible = createMemo(() =>
    rows()
      .filter((row) => roster.expanded() || row.pending)
      .map((row) => row.key),
  );
  const running = () => rows().filter((row) => row.running).length;
  const now = liveNow(() => running() > 0);
  createEffect(on(props.model.generation, () => setDetails(new Set<string>())));
  return (
    <Show when={count() > 0}>
      <section class="agent-roster agent-subagents" data-agent-subagents="" aria-label="Subagents">
        <roster.Header>
          <Show when={running() > 0}>
            <span class="ui-icon">
              <span class="agent-working-spinner" aria-hidden="true" />
            </span>
            <span>{running()} running</span>
          </Show>
        </roster.Header>
        <Show when={visible().length > 0}>
          <div class="agent-subagents-list">
            <For each={visible()}>
              {(key) => (
                <Switch>
                  <Match when={subagents().get(key)}>
                    {(entry) => (
                      <SubagentRow
                        entry={entry()}
                        now={now()}
                        session={props.model.session}
                        pending={
                          <NestedTranscript
                            entries={entry().pendingEntries!}
                            pendingOnly={true}
                            expandedDetails={details()}
                            keyboardRequestKey={props.model.keyboardRequestKey()}
                            onDetailsToggle={(id, open) => {
                              const next = new Set(details());
                              if (open) next.add(id);
                              else next.delete(id);
                              setDetails(next);
                            }}
                            onUpdatesToggle={(id, open) =>
                              props.model.setActivityExpanded(id, open)
                            }
                            session={props.model.session}
                          />
                        }
                      />
                    )}
                  </Match>
                  <Match when={tasks().get(key)}>
                    {(item) => <TaskRow item={item()} now={now()} />}
                  </Match>
                </Switch>
              )}
            </For>
          </div>
        </Show>
      </section>
    </Show>
  );
}
