import { type JSX, Match, Show, Switch } from "solid-js";
import "./agent-blocks.css";
import type { ClientSession } from "../bridge";
import { AgentChart } from "./AgentChart";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { AgentTable } from "./AgentTable";
import { blockHeight } from "./agent-block-layout";
import { type BlockSelection, noteBlockSelecting } from "./agent-block-selection";
import { agentBlock } from "./block-store";
import { attachAgentSelection } from "./composer-store";

/** A chart or table row: a fixed frame whose contents follow the block's latest state. */
export function AgentBlockEntry(props: {
  entry: AgentTranscriptEntry & { block: NonNullable<AgentTranscriptEntry["block"]> };
  session: ClientSession;
}): JSX.Element {
  const record = () => agentBlock(props.session, props.entry.block.key);
  const title = () => record()?.block.title ?? props.entry.summary ?? "";
  const updated = () => record()?.block.updatedAtMs;
  const select = (selection: BlockSelection): void =>
    attachAgentSelection(props.session, selection);
  const selecting = (active: boolean): void => noteBlockSelecting(props.entry.id, active);
  return (
    <article
      class="agent-entry agent-entry-block"
      style={{ height: `${blockHeight(props.entry.block.kind)}px` }}
    >
      <div class="agent-entry-head">
        <span class="agent-entry-label">{props.entry.label}</span>
        <span class="agent-block-title">{title()}</span>
        <Show when={updated()}>
          {(time) => (
            <span class="agent-entry-status">
              updated{" "}
              {new Date(time()).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}
            </span>
          )}
        </Show>
      </div>
      <div class="agent-block-body">
        <Switch>
          <Match when={record()?.block.chart}>
            {(chart) => (
              <AgentChart
                chart={chart()}
                id={record()!.block.id}
                title={title()}
                onSelect={select}
                onSelecting={selecting}
              />
            )}
          </Match>
          <Match when={record()?.block.table}>
            {(table) => (
              <AgentTable
                table={table()}
                id={record()!.block.id}
                title={title()}
                onSelect={select}
                onSelecting={selecting}
              />
            )}
          </Match>
        </Switch>
      </div>
    </article>
  );
}
