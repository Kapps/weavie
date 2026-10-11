import {
  ChevronLeft,
  ChevronRight,
  MessageSquarePlus,
  WandSparkles,
  Wrench,
  X,
} from "lucide-solid";
import { For, type JSX, Show } from "solid-js";
import { AgentMarkdown } from "../../agent/AgentMarkdown";
import type { ClientSession } from "../../bridge";
import { keyHint } from "../../commands/key-hint";
import { captureCommandRunnerFor } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import type { PlacedHint } from "./hint-anchor";
import type { HintSeverity } from "./review-hints-store";

export const SEVERITY_LABELS: Record<HintSeverity, string> = {
  bug: "Bug",
  warn: "Warning",
  nit: "Nit",
};

// Command, accessible label, icon, and visible text (null for icon-only buttons).
const ACTIONS = [
  [CommandIds.reviewHintsFix, "Fix", Wrench, "Fix"],
  [CommandIds.reviewHintsFixWith, "Fix with…", WandSparkles, "Fix with…"],
  [CommandIds.reviewHintsComment, "Comment on PR", MessageSquarePlus, "Comment"],
  [CommandIds.reviewHintsPrevious, "Previous hint", ChevronLeft, null],
  [CommandIds.reviewHintsNext, "Next hint", ChevronRight, null],
  [CommandIds.reviewHintsDismiss, "Dismiss hint", X, null],
] as const;

/** A hint title with its `code` spans rendered as code, matching the markdown body; unpaired backticks stay text. */
export function HintTitle(props: { text: string }): JSX.Element {
  const parts = () => {
    const split = props.text.split("`");
    return split.length % 2 === 1 ? split : [props.text];
  };
  return (
    <For each={parts()}>{(part, index) => (index() % 2 === 1 ? <code>{part}</code> : part)}</For>
  );
}

/** The collapsed hint after its line's end: one element, ellipsized to the editor's visible width. */
export function HintPill(props: { placed: PlacedHint }): JSX.Element {
  const hint = () => props.placed.hint;
  return (
    <div
      class={`weavie-hint-pill weavie-hint-${hint().severity}`}
      classList={{ "weavie-hint-changed": props.placed.changed }}
      data-hint-id={hint().id}
    >
      <b>✦ {SEVERITY_LABELS[hint().severity]}</b> <HintTitle text={hint().title} />
    </div>
  );
}

/** The expanded hint below its line: buttons only, since editor-hosted inputs never receive keystrokes. */
export function HintPopover(props: {
  placed: PlacedHint;
  session: ClientSession;
  /** Whether the file is in the session's pull request, so the hint can become a PR comment. */
  commentable: boolean;
  onPointer: (inside: boolean) => void;
}): JSX.Element {
  const hint = () => props.placed.hint;
  const run = captureCommandRunnerFor(props.session, new Map());
  return (
    // biome-ignore lint/a11y/noStaticElementInteractions: hover tracking only; every action is a button.
    <div
      class={`weavie-hint-hover weavie-hint-${hint().severity}`}
      onMouseDown={(event) => event.preventDefault()}
      onMouseEnter={() => props.onPointer(true)}
      onMouseLeave={() => props.onPointer(false)}
    >
      <div class="weavie-hint-meta">
        <b>{SEVERITY_LABELS[hint().severity]}</b>
        <span class="weavie-hint-who">✦ {hint().agent}</span>
        <Show when={hint().category}>{(category) => <span>{category()}</span>}</Show>
        <Show when={props.placed.changed}>
          <span title="This line was edited since the hint was made.">Changed</span>
        </Show>
        <span class="weavie-hint-actions">
          <For
            each={ACTIONS.filter(
              ([id]) => id !== CommandIds.reviewHintsComment || props.commentable,
            )}
          >
            {([id, label, Icon, text]) => (
              <button
                type="button"
                title={`${label}${keyHint(id)}`}
                aria-label={label}
                onClick={() => void run(id, undefined)}
              >
                <Icon size={13} />
                {text}
              </button>
            )}
          </For>
        </span>
      </div>
      <div class="weavie-hint-title">
        <HintTitle text={hint().title} />
      </div>
      <Show when={hint().body !== ""}>
        <div class="weavie-hint-text">
          <AgentMarkdown
            cacheKey={hint()}
            content={hint().body}
            renderMermaid={false}
            session={props.session}
          />
        </div>
      </Show>
    </div>
  );
}
