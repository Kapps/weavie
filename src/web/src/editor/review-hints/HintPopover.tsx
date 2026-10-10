import { ChevronLeft, ChevronRight, WandSparkles, Wrench, X } from "lucide-solid";
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

const ACTIONS = [
  [CommandIds.reviewHintsFix, "Fix", Wrench],
  [CommandIds.reviewHintsFixWith, "Fix with…", WandSparkles],
  [CommandIds.reviewHintsPrevious, "Previous hint", ChevronLeft],
  [CommandIds.reviewHintsNext, "Next hint", ChevronRight],
  [CommandIds.reviewHintsDismiss, "Dismiss hint", X],
] as const;

/** The expanded hint below its line: buttons only, since editor-hosted inputs never receive keystrokes. */
export function HintPopover(props: {
  placed: PlacedHint;
  session: ClientSession;
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
          <For each={ACTIONS}>
            {([id, label, Icon]) => (
              <button
                type="button"
                title={`${label}${keyHint(id)}`}
                aria-label={label}
                onClick={() => void run(id, undefined)}
              >
                <Icon size={13} />
                <Show
                  when={id === CommandIds.reviewHintsFix || id === CommandIds.reviewHintsFixWith}
                >
                  {label}
                </Show>
              </button>
            )}
          </For>
        </span>
      </div>
      <div class="weavie-hint-title">{hint().title}</div>
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
