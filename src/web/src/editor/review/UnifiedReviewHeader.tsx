import { Check, RotateCcw } from "lucide-solid";
import { For, type JSX } from "solid-js";
import { keyHint } from "../../commands/key-hint";
import { runCommandWithFeedback } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import { DiffStats } from "./DiffStats";
import { type ReviewOverview, reviewProgress } from "./review-store";

/** Review-wide actions and heading, over a strip mapping every file's share of the review. */
export function UnifiedReviewHeader(props: {
  overview: () => ReviewOverview;
  current: () => number | undefined;
  displayPath: (path: string) => string;
  onReveal: (index: number) => void;
}): JSX.Element {
  return (
    <header class="unified-review-header">
      <div class="unified-review-heading">
        <span class="unified-review-kicker">{props.overview().label || "Review"}</span>
        <strong>
          {props.overview().files.length} changed file
          {props.overview().files.length === 1 ? "" : "s"}
        </strong>
        <DiffStats added={props.overview().added} removed={props.overview().removed} />
      </div>
      <div class="unified-review-header-actions">
        <button
          type="button"
          class="unified-review-action"
          disabled={!props.overview().files.some((file) => (file.diff()?.rejected.length ?? 0) > 0)}
          title={`Undo rejection${keyHint(CommandIds.undoRevert)}`}
          onClick={() => void runCommandWithFeedback(CommandIds.undoRevert)}
        >
          <RotateCcw size="1em" /> Undo rejection
        </button>
        <button
          type="button"
          class="unified-review-action keep"
          disabled={!props.overview().fullyLoaded()}
          title={`Keep all changes and close diff${keyHint(CommandIds.keepAll)}`}
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
      <nav class="unified-review-map" aria-label="Review files">
        <For each={props.overview().files}>
          {(file, index) => {
            const progress = () => reviewProgress(file);
            const percent = () => Math.round(progress().fraction * 100);
            return (
              <button
                type="button"
                class="unified-review-map-file"
                classList={{ current: props.current() === index() }}
                style={`flex-grow:${progress().total};--reviewed:${percent()}%`}
                title={`${props.displayPath(file.summary().path)} +${file.summary().added} −${file.summary().removed} · ${percent()}% reviewed`}
                aria-current={props.current() === index() ? "true" : undefined}
                onClick={() => props.onReveal(index())}
              />
            );
          }}
        </For>
      </nav>
    </header>
  );
}
