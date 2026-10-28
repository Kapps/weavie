import { For, type JSX, Show } from "solid-js";
import type { AgentActionOption, ClientSession } from "../../bridge";
import { liveKeyLabel } from "../../commands/keys-live";
import { CommandIds } from "../../commands/types";
import {
  acceptOption,
  answerPlanReview,
  type FlushFile,
  type PlanReview,
  planReviewAnswering,
  planReviewsFor,
  rejectOption,
} from "./plan-review-store";

/** Answers the agent's pending plan reviews for the tab showing `path`, saving an edited plan first. */
export function PlanReviewBar(props: {
  session: ClientSession;
  path: string;
  flush: FlushFile;
}): JSX.Element {
  const reviews = (): PlanReview[] => planReviewsFor(props.session, props.path);
  // The commands answer the tab's newest review, so only it advertises their keys.
  const key = (review: PlanReview, action: AgentActionOption): string => {
    if (review.requestId !== reviews().at(-1)?.requestId) return "";
    if (action.id === acceptOption(review)) return liveKeyLabel(CommandIds.acceptAgentPlan);
    return action.id === rejectOption(review) ? liveKeyLabel(CommandIds.rejectAgentPlan) : "";
  };
  return (
    <Show when={reviews().length > 0}>
      <div class="plan-review-bar" role="toolbar" aria-label="Plan review">
        <For each={reviews()}>
          {(review) => (
            <div class="agent-approval-actions">
              <span class="plan-review-label">
                {review.editable
                  ? "Edit the plan, then answer the agent"
                  : "Answer the agent's plan"}
              </span>
              <For each={review.actions}>
                {(action) => (
                  <button
                    type="button"
                    disabled={planReviewAnswering(props.session, review)}
                    title={
                      key(review, action) === ""
                        ? action.label
                        : `${action.label} (${key(review, action)})`
                    }
                    onClick={() =>
                      void answerPlanReview(props.session, review, action.id, props.flush)
                    }
                  >
                    {action.label}
                    <Show when={key(review, action) !== ""}>
                      <kbd class="agent-key-chip">{key(review, action)}</kbd>
                    </Show>
                  </button>
                )}
              </For>
            </div>
          )}
        </For>
      </div>
    </Show>
  );
}
