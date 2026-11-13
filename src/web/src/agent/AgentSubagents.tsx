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
import { cardOf, openCard, openCardTitle, ownCardOnFocus } from "./agent-cards";
import { liveNow } from "./live-clock";
import type { AgentPaneModel } from "./pane-store";
import { SubagentStatus } from "./SubagentStatus";
import "./agent-subagents.css";

/** The session's subagent roster; pending requests remain visible when it is collapsed. */
export function AgentSubagents(props: { model: AgentPaneModel }): JSX.Element {
  const [expanded, setExpanded] = createSignal(false);
  const [details, setDetails] = createSignal<ReadonlySet<string>>(new Set());
  const running = () =>
    props.model.subagents().filter((entry) => entry.subagent?.state === "running").length;
  const now = liveNow(() => running() > 0);
  const entries = createMemo(() =>
    [...props.model.subagents()].sort(
      (a, b) => Number(b.subagent?.state === "running") - Number(a.subagent?.state === "running"),
    ),
  );
  const visible = () =>
    entries().filter((entry) => expanded() || (entry.pendingEntries?.length ?? 0) > 0);
  const title = () => `Expand or Collapse Subagents${liveKeyHint(CommandIds.toggleSubagents)}`;
  createEffect(
    on(props.model.generation, () => {
      setExpanded(false);
      setDetails(new Set<string>());
    }),
  );
  createEffect(() => setContext("agentSubagentsAvailable", entries().length > 0));
  onCleanup(() => setContext("agentSubagentsAvailable", false));
  onCleanup(
    registerCommand(CommandIds.toggleSubagents, (_args, context) => {
      if (context.session !== props.model.session || entries().length === 0) return false;
      setExpanded((value) => !value);
      return true;
    }),
  );

  return (
    <Show when={entries().length > 0}>
      <section class="agent-subagents" data-agent-subagents="" aria-label="Subagents">
        <button
          type="button"
          class="ui-row agent-subagents-toggle"
          aria-expanded={expanded()}
          aria-label={`${expanded() ? "Collapse" : "Expand"} Subagents`}
          title={title()}
          onClick={() => void runCommandWithFeedback(CommandIds.toggleSubagents)}
        >
          <DisclosureIcon />
          <span>Subagents</span>
          <span>{entries().length}</span>
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
                    <button
                      type="button"
                      class="ui-row agent-subagent-select"
                      aria-label={entry.subagent!.name}
                      title={openCardTitle()}
                      onClick={() => void openCard(cardOf(entry)!)}
                    >
                      <span class="agent-subagent-name">{entry.subagent!.name}</span>
                      <SubagentStatus info={entry.subagent!} now={now()} />
                    </button>
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
          </div>
        </Show>
      </section>
    </Show>
  );
}
