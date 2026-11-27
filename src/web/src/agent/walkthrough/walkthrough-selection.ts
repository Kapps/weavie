import { onCleanup } from "solid-js";
import type { ClientSession } from "../../bridge";
import { CommandIds } from "../../commands/types";
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
  let at: AskPlace | null = null;
  // A question is a /btw, so an agent without one can't take it.
  const actions = createSelectionActions(
    document.body,
    session,
    (owner, selection, comment) => (at === null ? null : askFrom(at)(owner, selection, comment)),
    () =>
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
    at = placed(place, quote);
    actions.show({ label: at.label, text }, point, () => {});
  };
  // Prose selections complete on pointer-up, or key-up with Shift; code blocks offer their own.
  const onProse = (event: Event): void => {
    if (event instanceof KeyboardEvent && !event.shiftKey) return;
    const selection = document.getSelection();
    const text = selection?.toString().trim() ?? "";
    if (selection === null || selection.isCollapsed || text === "") return;
    const range = selection.getRangeAt(0);
    const element = (node: Node) => (node instanceof Element ? node : node.parentElement);
    const host = element(range.endContainer)?.closest<HTMLElement>("[data-place]");
    if (host == null) return;
    const box = [...range.getClientRects()].at(-1) ?? range.getBoundingClientRect();
    offer(host.dataset.place!, text, null, { x: box.right, y: box.bottom });
  };
  const listen = (root: HTMLElement): (() => void) => {
    for (const type of ["pointerup", "keyup"]) root.addEventListener(type, onProse);
    return () => {
      for (const type of ["pointerup", "keyup"]) root.removeEventListener(type, onProse);
    };
  };
  return { actions, offer, listen };
}
