import { FileText, Rows3 } from "lucide-solid";
import { type JSX, Show } from "solid-js";
import { keyHint } from "../../commands/key-hint";
import { runCommandWithFeedback } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import type { ReviewView } from "../editor-controller";

/** The tab strip's review entry: opens Review Changes, then switches between its unified and file views. */
export function ReviewViewSwitch(props: { view: ReviewView | undefined }): JSX.Element {
  const open = (): void => void runCommandWithFeedback(CommandIds.reviewOpen);
  const segment = (target: ReviewView, label: string, icon: JSX.Element) => (
    <button
      type="button"
      classList={{ "editor-review-segment": true, "editor-review-open": target === "all" }}
      aria-label={label}
      aria-pressed={props.view === target}
      title={props.view === target ? label : `${label}${keyHint(CommandIds.reviewOpen)}`}
      onClick={() => props.view !== target && open()}
    >
      {icon}
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
          <Rows3 size={14} />
          Review Changes
        </button>
      }
    >
      <fieldset class="editor-review-action editor-review-switch" aria-label="Review view">
        {segment("all", "All changed files", <Rows3 size={14} />)}
        {segment("file", "Single file", <FileText size={14} />)}
      </fieldset>
    </Show>
  );
}
