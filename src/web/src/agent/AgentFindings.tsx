import { For, type JSX, Show } from "solid-js";
import { CommandIds } from "../commands/types";
import { AgentFindingRow } from "./AgentFindingRow";
import { createRoster } from "./AgentRoster";
import type { AgentPaneModel } from "./pane-store";

/** The findings the agent has pinned in this conversation, on screen above the composer while it works and after. */
export function AgentFindings(props: { model: AgentPaneModel }): JSX.Element {
  const count = () => props.model.findings().length;
  const roster = createRoster({
    model: props.model,
    label: "Findings",
    command: CommandIds.toggleFindings,
    available: "agentFindingsAvailable",
    count,
    startsExpanded: true,
  });
  return (
    <Show when={count() > 0}>
      <section class="agent-roster agent-findings" data-agent-findings="" aria-label="Findings">
        <roster.Header>{null}</roster.Header>
        <Show when={roster.expanded()}>
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
