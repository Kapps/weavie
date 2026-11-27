import { type JSX, Show } from "solid-js";
import type { ClientSession } from "../../bridge";
import { AsideEntry } from "../AgentAsideEntry";
import { createNestedExpansion } from "../nested-expansion";
import type { PlacedQuestion } from "./walkthrough-questions";

/** A question asked from a walkthrough: its BTW card, headed by the question, quoting what was selected. */
export function WalkthroughQuestion(props: {
  placed: PlacedQuestion;
  session: ClientSession;
}): JSX.Element {
  const expansion = createNestedExpansion(props.session);
  const entry = () => props.placed.question.entry;
  return (
    <div class="walkthrough-question" data-walkthrough-question={entry().conversationId}>
      <AsideEntry
        entry={entry()}
        label={entry().summary ?? "Question"}
        before={
          <>
            <Show when={props.placed.moved}>
              <p class="walkthrough-moved">Asked about text that has since changed</p>
            </Show>
            <blockquote class="walkthrough-quote">{props.placed.question.anchor.quote}</blockquote>
          </>
        }
        expandedDetails={expansion.expandedDetails()}
        onDetailsToggle={expansion.onDetailsToggle}
        onUpdatesToggle={expansion.onUpdatesToggle}
        keyboardRequestKey={null}
        session={props.session}
      />
    </div>
  );
}
