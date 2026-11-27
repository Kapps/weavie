import { runCommandInSessionWithFeedback } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import { quoteOf } from "../../editor/selection-excerpt";
import { nextId } from "../composer-state";
import type { SelectionSend } from "../selection-actions";

/** Where in a walkthrough a question is asked: the walkthrough, a place the page finds again, and what the
 * agent is told the selection is, e.g. `Walkthrough "Claims" · stop 2, Waiting room`. */
export interface AskPlace {
  conversationId: string | null;
  blockId: string;
  place: string;
  label: string;
  /** The quote the anchor keeps; null takes the selection's first line. */
  quote: string | null;
}

/** Send for a selection in a walkthrough: a side conversation forked from the working agent, anchored there. */
export const askFrom =
  (at: AskPlace): SelectionSend =>
  (session, selection, comment) => {
    const id = nextId("walkthrough");
    void runCommandInSessionWithFeedback(session, CommandIds.askAgentAside, {
      question: comment,
      submissionId: id,
      kind: "prompt",
      commandName: "",
      attachmentIds: [],
      selections: [{ id: `${id}-selection`, label: at.label, text: selection.text }],
      anchor: {
        path: null,
        conversationId: at.conversationId,
        blockId: at.blockId,
        place: at.place,
        quote: at.quote ?? quoteOf(selection.text),
        card: id,
      },
    });
  };
