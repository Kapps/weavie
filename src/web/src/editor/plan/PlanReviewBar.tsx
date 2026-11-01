import { For, type JSX, onCleanup, Show } from "solid-js";
import { ApprovalActions } from "../../agent/AgentPaneActions";
import { type AgentActionOption, type ClientSession, selectedSession } from "../../bridge";
import { setContext } from "../../commands/context";
import { createSessionFeatureValue } from "../../messaging/session-feature-value";
import { activeTabFor } from "../session-store";

/** A pending agent plan approval, answered from the editor tab of its plan file `path`. */
interface PlanReview {
  requestId: string;
  path: string;
  actions: AgentActionOption[];
}

const reviewsFor = createSessionFeatureValue<{ reviews: PlanReview[] }, PlanReview[]>(
  "planReview",
  "reviews",
  ({ reviews }) => reviews,
);

/** Answers the pending plan reviews of the active tab's plan file; approving saves the edited plan first. */
export function PlanReviewBar(props: {
  afterFlush: (session: ClientSession, path: string, action: string, send: () => void) => void;
}): JSX.Element {
  const reviews = (session: ClientSession): PlanReview[] => {
    const path = activeTabFor(session)?.entry.path;
    return (reviewsFor(session) ?? []).filter((review) => review.path === path);
  };
  return (
    <Show when={selectedSession()} keyed>
      {(session) => (
        <Show when={reviews(session).length > 0}>
          <div class="plan-review-bar" role="toolbar" aria-label="Plan review" ref={showKeys}>
            <For each={reviews(session)}>
              {(review) => (
                <div class="plan-review-row">
                  <span class="plan-review-label">Edit the plan, then answer the agent</span>
                  <ApprovalActions
                    session={session}
                    message={review}
                    answersToKeys={review.requestId === reviews(session).at(-1)?.requestId}
                    beforeAllow={(send) =>
                      props.afterFlush(session, review.path, "approving the plan", send)
                    }
                  />
                </div>
              )}
            </For>
          </div>
        </Show>
      )}
    </Show>
  );
}

// The approval chords reach the bar's newest review from the editor while the bar is shown.
function showKeys(): void {
  setContext("planReviewShown", true);
  onCleanup(() => setContext("planReviewShown", false));
}
