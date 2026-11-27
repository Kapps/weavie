import { onCleanup } from "solid-js";
import type { ClientSession } from "../../bridge";
import { CommandIds } from "../../commands/types";
import { watchDomSelectionGestures } from "../../editor/selection-gestures";
import { agentControlState } from "../agent-controls-store";
import { createSelectionActions, type SelectionActions } from "../selection-actions";
import { type AskPlace, askFrom } from "./walkthrough-ask";

/** Selecting text in a walkthrough offers the selection actions; Send asks about it where it was selected. */
export function createWalkthroughSelection(
  session: ClientSession,
  placed: (place: string, quote: string | null) => AskPlace,
): {
  actions: SelectionActions;
  offer: (
    place: string,
    text: string,
    quote: string | null,
    point: { x: number; y: number },
  ) => void;
  /** Offers prose selections inside `root`; the returned function stops. */
  listen: (root: HTMLElement) => () => void;
} {
  // A question is a /btw, so an agent without one can't take it.
  const actions = createSelectionActions(document.body, session, () =>
    agentControlState(session).slash.some((entry) => entry.commandId === CommandIds.askAgentAside)
      ? null
      : "This agent can't answer side questions",
  );
  onCleanup(actions.dispose);
  const offer = (
    place: string,
    text: string,
    quote: string | null,
    point: { x: number; y: number },
  ): void => {
    const at = placed(place, quote);
    actions.show({ label: at.label, text }, point, askFrom(at));
  };
  // Prose selections are offered here; code blocks, whose editors keep their own selection, offer theirs.
  const listen = (root: HTMLElement): (() => void) =>
    watchDomSelectionGestures(
      root,
      (range, text) => {
        const element = (node: Node) => (node instanceof Element ? node : node.parentElement);
        const host = element(range.endContainer)?.closest<HTMLElement>("[data-place]");
        if (host == null) return;
        const box = [...range.getClientRects()].at(-1) ?? range.getBoundingClientRect();
        offer(host.dataset.place!, text, null, { x: box.right, y: box.bottom });
      },
      () => {},
    );
  return { actions, offer, listen };
}
