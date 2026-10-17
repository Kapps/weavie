import { Files, FileText } from "lucide-solid";
import { type JSX, Show } from "solid-js";
import { keyHint } from "../../commands/key-hint";
import { runCommandWithFeedback } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import type { ReviewView } from "../editor-controller";

/** The tab strip's review entry: opens Review Changes, then switches between its unified and file views. */
export function ReviewViewSwitch(props: {
  view: ReviewView | undefined;
  count: number;
}): JSX.Element {
  const open = (): void => void runCommandWithFeedback(CommandIds.reviewOpen);
  const segment = (target: ReviewView, label: string, hint: string, icon: JSX.Element) => (
    <button
      type="button"
      classList={{ "editor-review-segment": true, "editor-review-open": target === "all" }}
      aria-pressed={props.view === target}
      title={props.view === target ? label : `${hint}${keyHint(CommandIds.reviewOpen)}`}
      onClick={() => props.view !== target && open()}
    >
      {icon}
      {label}
      <Show when={target === "all"}>
        <span class="editor-review-count">{props.count}</span>
      </Show>
    </button>
  );
  return (
    <Show
      when={props.view !== undefined}
      fallback={
        <button
          type="button"
          class="editor-review-action editor-review-open"
          title={`Review Changes${keyHint(CommandIds.reviewOpen)}`}
          onClick={open}
        >
          <Files size={14} />
          Review Changes
          <span class="editor-review-count">{props.count}</span>
        </button>
      }
    >
      <fieldset class="editor-review-action editor-review-switch" aria-label="Review view">
        {segment("all", "All files", "Show every changed file", <Files size={14} />)}
        {segment("file", "This file", "Open this change in file review", <FileText size={14} />)}
      </fieldset>
    </Show>
  );
}
