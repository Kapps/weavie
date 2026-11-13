import { For, type JSX } from "solid-js";
import type { ClientSession } from "../bridge";
import { createResizeDrag } from "../chrome/resize-drag";
import { AgentBlockEntry } from "./AgentBlockEntry";
import { AgentCardTabFrame } from "./AgentCardTabFrame";
import type { AgentBlockTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { chartHeight } from "./agent-block-layout";

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
  const heightOf = (id: string): number | null => props.viewState()?.heights[id] ?? null;
  const setHeight = (id: string, height: number): void =>
    props.setViewState({
      heights: { ...props.viewState()?.heights, [id]: Math.max(minimumHeight, height) },
    });
  return (
    <AgentCardTabFrame
      session={props.session}
      kind={props.kind}
      path={props.path}
      noun={props.kind}
      class="agent-blocks-tab"
      bind={props.bind}
    >
      {(card) => (
        <For
          each={
            (card().kind === "dashboard"
              ? (card().asideEntries ?? [])
              : [card()]) as AgentBlockTranscriptEntry[]
          }
        >
          {(block) => (
            <BlockRow
              block={block}
              session={props.session}
              height={heightOf(block.block.id)}
              setHeight={(height) => setHeight(block.block.id, height)}
            />
          )}
        </For>
      )}
    </AgentCardTabFrame>
  );
}

function BlockRow(props: {
  block: AgentBlockTranscriptEntry;
  session: ClientSession;
  height: number | null;
  setHeight: (height: number) => void;
}): JSX.Element {
  let frame!: HTMLDivElement;
  // Read only as a drag or a key starts, never while rendering.
  const current = (): number => props.height ?? frame.getBoundingClientRect().height;
  const drag = createResizeDrag({
    saved: current,
    save: props.setHeight,
    measure: (start) => {
      const from = current();
      return (move) => Math.max(minimumHeight, from + move.clientY - start.clientY);
    },
  });
  // Until the user sizes it, a block is as tall here as in the transcript.
  const height = (): number | null =>
    drag.dragging() || props.height !== null
      ? drag.size()
      : props.block.block.kind === "chart"
        ? chartHeight
        : null;
  return (
    <div class="agent-blocks-tab-row">
      <div ref={frame}>
        <AgentBlockEntry entry={props.block} session={props.session} height={height()} />
      </div>
      <button
        type="button"
        class="agent-blocks-tab-handle"
        aria-label={`Resize ${props.block.summary ?? props.block.label}`}
        title="Drag, or press ArrowUp or ArrowDown, to resize"
        onPointerDown={drag.start}
        onKeyDown={(event) => {
          if (event.key !== "ArrowUp" && event.key !== "ArrowDown") return;
          event.preventDefault();
          props.setHeight(current() + (event.key === "ArrowDown" ? keyStep : -keyStep));
        }}
      />
    </div>
  );
}
