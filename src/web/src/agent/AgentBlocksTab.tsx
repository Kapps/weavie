import { createMemo, For, type JSX, onCleanup, onMount, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { createResizeDrag } from "../chrome/resize-drag";
import { AgentBlockEntry } from "./AgentBlockEntry";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { chartHeight, tableHeightEstimate } from "./agent-block-layout";
import { cardForTabEntry } from "./card-tab";
import { agentPaneModel } from "./pane-store";

type BlockEntry = AgentTranscriptEntry & { block: NonNullable<AgentTranscriptEntry["block"]> };

/** What a blocks tab saves with its tab: each block's height as the user sized it, by block id. */
export interface BlocksTabViewState {
  heights: Record<string, number>;
}

const minimumHeight = 160;
const keyStep = 40;

/** A dashboard's blocks, or one block, one per row at editor width; each row's height is the user's to drag. */
export default function AgentBlocksTab(props: {
  session: ClientSession;
  kind: "dashboard" | "block";
  path: string;
  viewState: () => BlocksTabViewState | null;
  setViewState: (state: BlocksTabViewState) => void;
  bind(element: HTMLElement): () => void;
}): JSX.Element {
  let host!: HTMLDivElement;
  const entry = createMemo(() => cardForTabEntry(props.session, props.kind, props.path));
  const blocks = (): BlockEntry[] => {
    const card = entry();
    if (card === undefined) return [];
    return (card.kind === "dashboard" ? (card.asideEntries ?? []) : [card]) as BlockEntry[];
  };
  const heightOf = (id: string): number | null => props.viewState()?.heights[id] ?? null;
  const setHeight = (id: string, height: number): void =>
    props.setViewState({
      heights: { ...props.viewState()?.heights, [id]: Math.max(minimumHeight, height) },
    });
  onMount(() => onCleanup(props.bind(host)));

  return (
    <div
      class="editor-agent-view agent-body agent-blocks-tab"
      data-kind="editor"
      tabindex="0"
      ref={host}
    >
      <Show
        when={entry()}
        fallback={
          <Show when={agentPaneModel(props.session)?.historyComplete()}>
            <div class="editor-plan-notice">
              This {props.kind === "dashboard" ? "dashboard" : "block"} is no longer in the
              transcript.
            </div>
          </Show>
        }
      >
        <For each={blocks()}>
          {(block) => (
            <BlockRow
              block={block}
              session={props.session}
              height={heightOf(block.block.id)}
              setHeight={(height) => setHeight(block.block.id, height)}
            />
          )}
        </For>
      </Show>
    </div>
  );
}

function BlockRow(props: {
  block: BlockEntry;
  session: ClientSession;
  height: number | null;
  setHeight: (height: number) => void;
}): JSX.Element {
  // Until the user sizes it, a block is as tall here as in the transcript.
  const height = () =>
    props.height ?? (props.block.block.kind === "chart" ? chartHeight : tableHeightEstimate);
  const drag = createResizeDrag({
    saved: height,
    save: props.setHeight,
    measure: (start) => {
      const from = height();
      return (move) => Math.max(minimumHeight, from + move.clientY - start.clientY);
    },
  });
  return (
    <div class="agent-blocks-tab-row">
      <AgentBlockEntry entry={props.block} session={props.session} height={drag.size()} />
      {/* A drag resizes the block, and so do ArrowUp and ArrowDown while the handle has focus. */}
      <button
        type="button"
        class="agent-blocks-tab-handle"
        aria-label={`Resize ${props.block.summary ?? props.block.label}`}
        title="Resize"
        onPointerDown={drag.start}
        onKeyDown={(event) => {
          if (event.key !== "ArrowUp" && event.key !== "ArrowDown") return;
          event.preventDefault();
          props.setHeight(height() + (event.key === "ArrowDown" ? keyStep : -keyStep));
        }}
      />
    </div>
  );
}
