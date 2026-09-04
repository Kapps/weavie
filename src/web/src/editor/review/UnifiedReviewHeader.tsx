import { Check, ChevronDown, ChevronUp, RotateCcw } from "lucide-solid";
import type { JSX } from "solid-js";
import { keyHint } from "../../commands/key-hint";
import { runCommandWithFeedback } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import type { ReviewOverview } from "./review-store";

/** The overview's own toolbar: where the walk is, how to step it, and the two set-wide review actions. */
export function UnifiedReviewHeader(props: { overview: () => ReviewOverview }): JSX.Element {
  return (
    <header class="unified-review-header">
      <div class="unified-review-heading">
        <span class="unified-review-kicker">{props.overview().label || "Review"}</span>
        <strong>Unified review</strong>
      </div>
      <div class="unified-review-header-actions">
        <button
          type="button"
          class="unified-review-action"
          title={`Previous change${keyHint(CommandIds.prevChange)}`}
          onClick={() => void runCommandWithFeedback(CommandIds.prevChange)}
        >
          <ChevronUp size="1em" /> Prev
        </button>
        <button
          type="button"
          class="unified-review-action"
          title={`Next change${keyHint(CommandIds.nextChange)}`}
          onClick={() => void runCommandWithFeedback(CommandIds.nextChange)}
        >
          <ChevronDown size="1em" /> Next
        </button>
        <button
          type="button"
          class="unified-review-action keep"
          disabled={!props.overview().fullyLoaded()}
          title={`Keep all changes${keyHint(CommandIds.keepAll)}`}
          onClick={() => void runCommandWithFeedback(CommandIds.keepAll)}
        >
          <Check size="1em" /> Keep all
        </button>
        <button
          type="button"
          class="unified-review-action revert"
          disabled={!props.overview().fullyLoaded() || !props.overview().hasPending()}
          title={`Revert every pending change${keyHint(CommandIds.undoChange)}`}
          onClick={() => void runCommandWithFeedback(CommandIds.undoChange)}
        >
          <RotateCcw size="1em" /> Revert pending
        </button>
      </div>
    </header>
  );
}
