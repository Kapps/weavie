import { ArrowUp, Check, RotateCcw } from "lucide-solid";
import { type JSX, Show } from "solid-js";
import type { ClientSession } from "../../bridge";
import { keyHint } from "../../commands/key-hint";
import { runCommandWithFeedback } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import { FinishReview } from "../pr-comments/FinishReview";
import { DiffStats } from "./DiffStats";
import { PrStatePill } from "./overview/OverviewTitle";
import { prOverviewFor } from "./overview/overview-data";
import type { ReviewOverview } from "./review-store";

/** Review-wide actions and heading; once the overview title scrolls away, it carries the session's identity. */
export function UnifiedReviewHeader(props: {
  overview: () => ReviewOverview;
  session: ClientSession;
  showIdentity: () => boolean;
  onOverview: () => void;
}): JSX.Element {
  const detail = () => prOverviewFor(props.session)?.detail ?? null;
  const failing = () => detail()?.checks.filter((check) => check.state === "failure").length ?? 0;
  return (
    <header class="unified-review-header">
      <div class="unified-review-heading">
        <Show
          when={props.showIdentity()}
          fallback={<span class="unified-review-kicker">{props.overview().label || "Review"}</span>}
        >
          <button
            type="button"
            class="unified-review-action ui-button"
            title={`Show the session overview${keyHint(CommandIds.reviewOverview)}`}
            onClick={() => props.onOverview()}
          >
            <span class="ui-icon">
              <ArrowUp />
            </span>
            Overview
          </button>
          <Show when={detail()}>
            {(pull) => (
              <span class="unified-review-identity">
                <PrStatePill session={props.session} detail={pull()} />
                <span class="unified-review-identity-title">{pull().title}</span>
                <span class="unified-review-status">#{pull().number}</span>
                <Show when={failing() > 0}>
                  <span class="unified-review-failing">
                    {failing()} check{failing() === 1 ? "" : "s"} failing
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
        <button
          type="button"
          class="unified-review-action ui-button"
          disabled={!props.overview().files.some((file) => (file.diff()?.rejected.length ?? 0) > 0)}
          title={`Undo rejection${keyHint(CommandIds.undoRevert)}`}
          aria-label="Undo rejection"
          onClick={() => void runCommandWithFeedback(CommandIds.undoRevert)}
        >
          <span class="ui-icon">
            <RotateCcw />
          </span>
          <Show when={!props.showIdentity()}> Undo rejection</Show>
        </button>
        <button
          type="button"
          class="unified-review-action keep ui-button"
          disabled={!props.overview().fullyLoaded()}
          title={`Keep all changes and close diff${keyHint(CommandIds.keepAll)}`}
          onClick={() => void runCommandWithFeedback(CommandIds.keepAll)}
        >
          <span class="ui-icon">
            <Check />
          </span>
          Keep all
        </button>
        <button
          type="button"
          class="unified-review-action revert ui-button"
          disabled={!props.overview().fullyLoaded() || !props.overview().hasPending()}
          title={`Revert every pending change${keyHint(CommandIds.undoChange)}`}
          aria-label="Revert pending"
          onClick={() => void runCommandWithFeedback(CommandIds.undoChange)}
        >
          <span class="ui-icon">
            <RotateCcw />
          </span>
          <Show when={!props.showIdentity()}> Revert pending</Show>
        </button>
        <Show when={props.showIdentity()}>
          <FinishReview session={props.session} />
        </Show>
      </div>
    </header>
  );
}
