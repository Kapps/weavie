import { ArrowUp, CheckCheck, CircleX, RotateCcw, Undo2 } from "lucide-solid";
import { type JSX, Show } from "solid-js";
import type { ClientSession } from "../../bridge";
import { keyHint } from "../../commands/key-hint";
import { runCommandWithFeedback } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import { FinishReview } from "../pr-comments/FinishReview";
import { prCommentsFor } from "../pr-comments/pr-comments-store";
import { reviewHintsFor } from "../review-hints/review-hints-store";
import { ReviewItemStepper } from "../review-items/ReviewItemStepper";
import { DiffStats } from "./DiffStats";
import { PrStatePill } from "./overview/OverviewTitle";
import { prOverviewFor } from "./overview/overview-data";
import type { ReviewOverview } from "./review-store";

/**
 * Review-wide actions, as one row of icon tools, and the heading. Once the overview title scrolls away the heading
 * carries the session's identity and the row adds the review-submit control.
 */
export function UnifiedReviewHeader(props: {
  overview: () => ReviewOverview;
  session: ClientSession;
  showIdentity: () => boolean;
  onOverview: () => void;
}): JSX.Element {
  const detail = () => prOverviewFor(props.session)?.detail ?? null;
  const failing = () => detail()?.checks.filter((check) => check.state === "failure").length ?? 0;
  const items = () =>
    prCommentsFor(props.session)?.set != null || (reviewHintsFor(props.session)?.length ?? 0) > 0;
  const tool = (spec: {
    label: string;
    command: string;
    tone: "" | "keep" | "revert";
    disabled: () => boolean;
    icon: JSX.Element;
  }) => (
    <button
      type="button"
      class={`ui-icon-button review-tool ${spec.tone}`}
      disabled={spec.disabled()}
      title={`${spec.label}${keyHint(spec.command)}`}
      aria-label={spec.label}
      onClick={() => void runCommandWithFeedback(spec.command)}
    >
      <span class="ui-icon">{spec.icon}</span>
    </button>
  );
  return (
    <header class="unified-review-header">
      <div class="unified-review-heading">
        <Show
          when={props.showIdentity()}
          fallback={<span class="unified-review-kicker">{props.overview().label || "Review"}</span>}
        >
          <button
            type="button"
            class="ui-icon-button review-tool"
            title={`Show Session Overview${keyHint(CommandIds.reviewOverview)}`}
            aria-label="Overview"
            onClick={() => props.onOverview()}
          >
            <span class="ui-icon">
              <ArrowUp />
            </span>
          </button>
          <Show when={detail()}>
            {(pull) => (
              <span class="unified-review-identity">
                <PrStatePill session={props.session} detail={pull()} />
                <span class="unified-review-identity-title">{pull().title}</span>
                <span class="unified-review-status">#{pull().number}</span>
                <Show when={failing() > 0}>
                  <span
                    class="unified-review-failing"
                    title={`${failing()} check${failing() === 1 ? "" : "s"} failing`}
                  >
                    <span class="ui-icon">
                      <CircleX />
                    </span>
                    {failing()}
                  </span>
                </Show>
              </span>
            )}
          </Show>
        </Show>
        {/* The PR's name takes the counts' room; the overview and each file header still carry them. */}
        <Show when={!props.showIdentity() || detail() === null}>
          <strong>
            {props.overview().files.length} changed file
            {props.overview().files.length === 1 ? "" : "s"}
          </strong>
          <DiffStats added={props.overview().added} removed={props.overview().removed} />
        </Show>
      </div>
      <div class="unified-review-header-actions">
        <Show when={items()}>
          <ReviewItemStepper session={props.session} />
          <span class="unified-review-header-rule" />
        </Show>
        {tool({
          label: "Undo Revert (Review)",
          command: CommandIds.undoRevert,
          tone: "",
          disabled: () =>
            !props.overview().files.some((file) => (file.diff()?.rejected.length ?? 0) > 0),
          icon: <Undo2 />,
        })}
        {tool({
          label: "Undo All Changes",
          command: CommandIds.undoChange,
          tone: "revert",
          disabled: () => !props.overview().fullyLoaded() || !props.overview().hasPending(),
          icon: <RotateCcw />,
        })}
        {tool({
          label: "Keep All Changes (Review)",
          command: CommandIds.keepAll,
          tone: "keep",
          disabled: () => !props.overview().fullyLoaded(),
          icon: <CheckCheck />,
        })}
        <Show when={props.showIdentity() && prCommentsFor(props.session)?.set != null}>
          <span class="unified-review-header-rule" />
          <FinishReview session={props.session} iconOnly={true} />
        </Show>
      </div>
    </header>
  );
}
