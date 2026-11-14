import {
  createEffect,
  createMemo,
  createSignal,
  For,
  type JSX,
  Match,
  on,
  onCleanup,
  Show,
  Switch,
} from "solid-js";
import { setContext } from "../commands/context";
import { liveKeyHint } from "../commands/keys-live";
import { registerCommand, runCommandWithFeedback } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { DisclosureIcon } from "./AgentDisclosure";
import { NestedTranscript } from "./AgentNestedCard";
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
  const [expanded, setExpanded] = createSignal(false);
  const [details, setDetails] = createSignal<ReadonlySet<string>>(new Set());
  let toggle: HTMLButtonElement | undefined;
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
  // Keys, not items: every update replaces the items, and a row must keep its focus across one.
  const visible = createMemo(() =>
    rows()
      .filter((row) => expanded() || row.pending)
      .map((row) => row.key),
  );
  const count = () => rows().length;
  const running = () => rows().filter((row) => row.running).length;
  const now = liveNow(() => running() > 0);
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
                            currentThought={null}
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
