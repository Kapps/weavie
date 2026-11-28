import { createEffect, createSignal, For, type JSX, on, onCleanup, Show } from "solid-js";
import { setContext } from "../commands/context";
import { liveKeyHint } from "../commands/keys-live";
import { registerCommand, runCommandWithFeedback } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { DisclosureIcon } from "./AgentDisclosure";
import { AgentFindingRow } from "./AgentFindingRow";
import type { AgentPaneModel } from "./pane-store";

/** The findings the agent has pinned in this conversation, on screen above the composer while it works and after. */
export function AgentFindings(props: { model: AgentPaneModel }): JSX.Element {
  const [expanded, setExpanded] = createSignal(true);
  let toggle: HTMLButtonElement | undefined;
  const count = () => props.model.findings().length;
  createEffect(on(props.model.generation, () => setExpanded(true)));
  createEffect(() => setContext("agentFindingsAvailable", count() > 0));
  onCleanup(() => setContext("agentFindingsAvailable", false));
  onCleanup(
    registerCommand(CommandIds.toggleFindings, (_args, context) => {
      if (context.session !== props.model.session || count() === 0) return false;
      setExpanded((value) => !value);
      toggle?.focus({ preventScroll: true });
      return true;
    }),
  );
  return (
    <Show when={count() > 0}>
      <section class="agent-findings" data-agent-findings="" aria-label="Findings">
        <button
          ref={toggle}
          type="button"
          class="ui-row agent-findings-toggle"
          aria-expanded={expanded()}
          aria-label={`${expanded() ? "Collapse" : "Expand"} Findings`}
          title={`Expand or Collapse Findings${liveKeyHint(CommandIds.toggleFindings)}`}
          onClick={() => void runCommandWithFeedback(CommandIds.toggleFindings)}
        >
          <DisclosureIcon />
          <span>Findings</span>
          <span>{count()}</span>
        </button>
        <Show when={expanded()}>
          <div class="agent-findings-scroll">
            <div class="agent-findings-list">
              <For each={props.model.findings()}>
                {(entry) => (
                  <AgentFindingRow
                    session={props.model.session}
                    blockKey={entry.block.key}
                    placement={`board:${entry.block.key}`}
                    title={entry.summary ?? ""}
                  />
                )}
              </For>
            </div>
          </div>
        </Show>
      </section>
    </Show>
  );
}
