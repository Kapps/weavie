import type { ClientSession } from "../../bridge";
import { runCommandInSessionWithFeedback } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import type { SelectionSend } from "../selection-actions";

/** Where in a walkthrough a question is asked: the block and a place the page can find again. */
export interface WalkthroughAnchor {
  blockId: string;
  place: string;
  /** What the agent is told the selection is, e.g. `Walkthrough "Claims" · stop 2, Waiting room`. */
  label: string;
}

let sequence = 0;

/** Send for a selection in a walkthrough: a side conversation forked from the working agent, anchored there. */
export const askFrom =
  (anchor: WalkthroughAnchor): SelectionSend =>
  (session, selection, comment) => {
    const id = `walkthrough-${Date.now().toString(36)}-${(++sequence).toString(36)}`;
    void runCommandInSessionWithFeedback(session, CommandIds.askAgentAside, {
      question: comment,
      submissionId: id,
      kind: "prompt",
      commandName: "",
      attachmentIds: [],
      selections: [{ id: `${id}-selection`, label: anchor.label, text: selection.text }],
      anchor: { blockId: anchor.blockId, place: anchor.place, quote: selection.text },
    });
    return id;
  };

/** Stops the turn side conversation `conversationId` is working on. */
export const stopAside = (session: ClientSession, conversationId: string): void =>
  session.feature("agent").publish("stopAside", { conversationId });
