import { Paperclip } from "lucide-solid";
import { type JSX, Match, onCleanup, Show, Switch, useContext } from "solid-js";
import "./agent-blocks.css";
import type { ClientSession } from "../bridge";
import { keyHint } from "../commands/key-hint";
import { CommandIds } from "../commands/types";
import { AgentChart } from "./AgentChart";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { AgentTable } from "./AgentTable";
import { chartHeight } from "./agent-block-layout";
import { noteBlockSelecting, wholeBlockSelection } from "./agent-block-selection";
import { cardOf, openCard, ownCardOnFocus } from "./agent-cards";
import { ownCommand, ownsCommand } from "./agent-command-owner";
import { AgentRowContext } from "./agent-row-context";
import { agentBlock, tableView } from "./block-store";
import type { AgentSelection } from "./composer-state";
import { attachAgentSelection } from "./composer-store";

/**
 * A chart or table: a fixed frame whose contents follow the block's latest state. `height` is the frame's height in
 * an editor tab, where the user sizes it; null keeps the transcript's.
 */
export function AgentBlockEntry(props: {
  entry: AgentTranscriptEntry & { block: NonNullable<AgentTranscriptEntry["block"]> };
  session: ClientSession;
  height: number | null;
}): JSX.Element {
  const row = useContext(AgentRowContext);
  // Only a block that is its own transcript row opens alone; a dashboard's opens with its card.
  const card = row?.rowId === props.entry.id ? cardOf(props.entry) : null;
  const record = () => agentBlock(props.session, props.entry.block.key);
  const view =
    props.entry.block.kind === "table" ? tableView(props.session, props.entry.id) : undefined;
  const title = () => record()?.block.title ?? props.entry.summary ?? "";
  const select = (selection: AgentSelection): void =>
    attachAgentSelection(props.session, selection);
  // The row holding the block stays mounted mid-gesture; in a tab nothing unmounts it.
  const selecting = (active: boolean): void => {
    if (row !== undefined) noteBlockSelecting(row.rowId, active);
  };
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
  const ownCard = ownCardOnFocus(() =>
    card === null ? null : { session: props.session, card, toggle: null },
  );
  const press = (event: Event): void => {
    own(event);
    ownCard(event);
  };
  return (
    <div
      class="agent-entry agent-entry-block"
      data-block={props.entry.block.kind}
      style={{
        height:
          props.height !== null
            ? `${props.height}px`
            : props.entry.block.kind === "chart"
              ? `${chartHeight}px`
              : undefined,
      }}
      onFocusIn={press}
      onPointerDown={press}
    >
      <div class="agent-entry-head">
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
        <Show when={card}>
          {(open) => (
            <button
              type="button"
              class="agent-entry-rewind agent-entry-open"
              title={`Open Card in Tab${keyHint(CommandIds.openAgentCard)}`}
              onClick={() => void openCard(open())}
            >
              Open
            </button>
          )}
        </Show>
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
          <Match when={view !== undefined && record()?.block.table}>
            {(table) => (
              <AgentTable
                table={table()}
                view={view!}
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
