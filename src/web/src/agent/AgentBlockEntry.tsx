import { Paperclip } from "lucide-solid";
import { type JSX, Match, onCleanup, Show, Switch } from "solid-js";
import "./agent-blocks.css";
import type { ClientSession } from "../bridge";
import { keyHint } from "../commands/key-hint";
import { CommandIds } from "../commands/types";
import { AgentChart } from "./AgentChart";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { AgentTable } from "./AgentTable";
import { blockHeight } from "./agent-block-layout";
import { noteBlockSelecting, wholeBlockSelection } from "./agent-block-selection";
import { ownCommand, ownsCommand } from "./agent-command-owner";
import { agentBlock } from "./block-store";
import type { AgentSelection } from "./composer-state";
import { attachAgentSelection } from "./composer-store";

/** A chart or table row: a fixed frame whose contents follow the block's latest state. */
export function AgentBlockEntry(props: {
  entry: AgentTranscriptEntry & { block: NonNullable<AgentTranscriptEntry["block"]> };
  session: ClientSession;
}): JSX.Element {
  const record = () => agentBlock(props.session, props.entry.block.key);
  const title = () => record()?.block.title ?? props.entry.summary ?? "";
  const select = (selection: AgentSelection): void =>
    attachAgentSelection(props.session, selection);
  const selecting = (active: boolean): void => noteBlockSelecting(props.entry.id, active);
  const attachAll = (): boolean => {
    const block = record()?.block;
    if (block === undefined) return false;
    select(wholeBlockSelection(block));
    return true;
  };
  // The block last pressed or focused owns Attach Block to Prompt, until the user moves on; the omnibar keeps it.
  let release: (() => void) | null = null;
  const own = (event: Event): void => {
    if (ownsCommand(CommandIds.attachAgentBlock, release)) return;
    release = ownCommand(
      CommandIds.attachAgentBlock,
      "agentBlockFocused",
      event.currentTarget as Element,
      attachAll,
      false,
    );
  };
  onCleanup(() => release?.());
  return (
    <div
      class="agent-entry agent-entry-block"
      style={{ height: `${blockHeight(props.entry.block.kind)}px` }}
      onFocusIn={own}
      onPointerDown={own}
    >
      <div class="agent-entry-head">
        <span class="agent-entry-label">{props.entry.label}</span>
        <span class="agent-block-title">{title()}</span>
        <button
          type="button"
          class="ui-icon-button agent-block-attach"
          title={`Attach Block to Prompt${keyHint(CommandIds.attachAgentBlock)}`}
          aria-label="Attach Block to Prompt"
          onClick={attachAll}
        >
          <span class="ui-icon">
            <Paperclip />
          </span>
        </button>
        <Show when={record()?.block.updatedAtMs}>
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
    </div>
  );
}
