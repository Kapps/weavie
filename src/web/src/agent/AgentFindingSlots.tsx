import { Show } from "solid-js";
import { render } from "solid-js/web";
import type { ClientSession } from "../bridge";
import { AgentFindingRow } from "./AgentFindingRow";
import { agentBlock } from "./block-store";

/**
 * Shows each slot under `root` as its finding in the conversation `scope` names; an id that names no finding stays
 * as the text the agent wrote. Returns a function that unmounts them.
 */
export function hydrateFindingSlots(
  root: HTMLElement,
  session: ClientSession,
  { entryId, scope }: { entryId: string; scope: string },
): () => void {
  const disposers = [...root.querySelectorAll<HTMLElement>(".agent-finding-slot")].map((slot) => {
    const id = slot.dataset.finding!;
    const literal = slot.textContent;
    slot.textContent = "";
    return render(
      () => (
        <Show when={agentBlock(session, scope + id)?.block.finding} fallback={literal}>
          <AgentFindingRow
            session={session}
            blockKey={scope + id}
            placement={`${entryId}:${id}`}
            title={literal}
          />
        </Show>
      ),
      slot,
    );
  });
  return () => {
    for (const dispose of disposers) dispose();
  };
}
