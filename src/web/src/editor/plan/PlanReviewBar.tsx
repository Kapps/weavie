import { For, type JSX, onCleanup, Show } from "solid-js";
import { KeyedButton } from "../../agent/AgentPaneActions";
import type { AgentActionOption, ClientSession } from "../../bridge";
import { liveKeyLabel } from "../../commands/keys-live";
import { CommandIds } from "../../commands/types";
import {
  type AfterFlush,
  acceptOption,
  answerPlanReview,
  type PlanReview,
  planReviewAnswering,
  planReviewsFor,
  rejectOption,
} from "./plan-review-store";

/** Answers the agent's pending plan reviews for the tab showing `path`, saving an edited plan first. */
export function PlanReviewBar(props: {
  session: ClientSession;
  path: string;
  afterFlush: AfterFlush;
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
      <div class="plan-review-bar" role="toolbar" aria-label="Plan review" ref={reserveSpace}>
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
                  <KeyedButton
                    label={action.label}
                    keyLabel={key(review, action)}
                    disabled={planReviewAnswering(props.session, review)}
                    onClick={() =>
                      void answerPlanReview(props.session, review, action.id, props.afterFlush)
                    }
                  />
                )}
              </For>
            </div>
          )}
        </For>
      </div>
    </Show>
  );
}

// The pane lays the editor out above the bar, so no plan line can sit underneath it.
function reserveSpace(bar: HTMLElement): void {
  let pane: HTMLElement | null = null;
  const observer = new ResizeObserver(() => {
    pane = bar.closest<HTMLElement>(".editor-pane");
    pane?.style.setProperty("--plan-review-height", `${bar.offsetHeight}px`);
  });
  observer.observe(bar);
  onCleanup(() => {
    observer.disconnect();
    pane?.style.removeProperty("--plan-review-height");
  });
}
