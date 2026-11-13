import { ArrowUp, Check, CheckCheck, CircleX, RotateCcw, Undo2 } from "lucide-solid";
import { type JSX, Show } from "solid-js";
import type { ClientSession } from "../../bridge";
import { keyHint } from "../../commands/key-hint";
import { runCommandWithFeedback } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import { FinishReview } from "../pr-comments/FinishReview";
import { ReviewItemStepper } from "../review-items/ReviewItemStepper";
import { DiffStats } from "./DiffStats";
import { PrStatePill } from "./overview/OverviewTitle";
import { prOverviewFor } from "./overview/overview-data";
import type { ReviewOverview } from "./review-store";

/**
 * Review-wide actions and heading. Once the overview title scrolls away it carries the session's identity, and the
 * actions condense to icons so the PR's name keeps its room.
 */
export function UnifiedReviewHeader(props: {
  overview: () => ReviewOverview;
  session: ClientSession;
  showIdentity: () => boolean;
  onOverview: () => void;
}): JSX.Element {
  const detail = () => prOverviewFor(props.session)?.detail ?? null;
  const failing = () => detail()?.checks.filter((check) => check.state === "failure").length ?? 0;
  const compact = () => props.showIdentity();
  const action = (spec: {
    label: string;
    title: string;
    command: string;
    tone: "" | "keep" | "revert";
    disabled: () => boolean;
    icon: JSX.Element;
    /** The icon once condensed, when the labelled one would read differently alone. */
    compactIcon?: JSX.Element;
  }) => (
    <button
      type="button"
      class={`unified-review-action ${spec.tone}`}
      classList={{ "ui-button": !compact(), "ui-icon-button": compact() }}
      disabled={spec.disabled()}
      title={`${spec.title}${keyHint(spec.command)}`}
      aria-label={spec.label}
      onClick={() => void runCommandWithFeedback(spec.command)}
    >
      <span class="ui-icon">{compact() ? (spec.compactIcon ?? spec.icon) : spec.icon}</span>
      <Show when={!compact()}> {spec.label}</Show>
    </button>
  );
  return (
    <header
      class="unified-review-header"
      classList={{ "unified-review-header-compact": compact() }}
    >
      <div class="unified-review-heading">
        <Show
          when={compact()}
          fallback={<span class="unified-review-kicker">{props.overview().label || "Review"}</span>}
        >
          <button
            type="button"
            class="unified-review-action ui-icon-button"
            title={`Show the session overview${keyHint(CommandIds.reviewOverview)}`}
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
        <Show when={!compact() || detail() === null}>
          <strong>
            {props.overview().files.length} changed file
            {props.overview().files.length === 1 ? "" : "s"}
          </strong>
          <DiffStats added={props.overview().added} removed={props.overview().removed} />
        </Show>
      </div>
      <div class="unified-review-header-actions">
        <ReviewItemStepper session={props.session} />
        {action({
          label: "Undo rejection",
          title: "Undo rejection",
          command: CommandIds.undoRevert,
          tone: "",
          disabled: () =>
            !props.overview().files.some((file) => (file.diff()?.rejected.length ?? 0) > 0),
          icon: <Undo2 />,
        })}
        {action({
          label: "Revert pending",
          title: "Revert every pending change",
          command: CommandIds.undoChange,
          tone: "revert",
          disabled: () => !props.overview().fullyLoaded() || !props.overview().hasPending(),
          icon: <RotateCcw />,
        })}
        {action({
          label: "Keep all",
          title: "Keep all changes and close diff",
          command: CommandIds.keepAll,
          tone: "keep",
          disabled: () => !props.overview().fullyLoaded(),
          icon: <Check />,
          compactIcon: <CheckCheck />,
        })}
        <Show when={compact()}>
          <span class="unified-review-header-rule" />
          <FinishReview session={props.session} />
        </Show>
      </div>
    </header>
  );
}
