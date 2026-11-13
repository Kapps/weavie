import type { JSX } from "solid-js";
import type { AgentCardRef } from "../editor/tab-entry";
import { openCard, openCardTitle } from "./agent-cards";

/** A card's Open: its content in an editor tab. */
export function AgentCardOpenButton(props: { card: AgentCardRef }): JSX.Element {
  return (
    <button
      type="button"
      class="agent-entry-rewind agent-entry-open"
      title={openCardTitle()}
      onClick={() => void openCard(props.card)}
    >
      Open
    </button>
  );
}
