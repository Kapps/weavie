import { ChevronLeft, ChevronRight } from "lucide-solid";
import { For, type JSX, Show } from "solid-js";
import type { WalkthroughStop } from "../../bridge";
import { keyHint } from "../../commands/key-hint";
import { CommandIds } from "../../commands/types";

/** The stops along the bottom of a walkthrough: where the reader is, what they have read, and what changed. */
export function WalkthroughStrip(props: {
  stops: readonly WalkthroughStop[];
  current: number;
  read: readonly string[];
  go: (index: number) => void;
}): JSX.Element {
  return (
    <nav class="walkthrough-strip" aria-label="Stops">
      <span class="ui-stepper">
        <button
          type="button"
          title={`Previous Walkthrough Stop${keyHint(CommandIds.previousWalkthroughStop)}`}
          aria-label="Previous Stop"
          disabled={props.current === 0}
          onClick={() => props.go(props.current - 1)}
        >
          <ChevronLeft class="ui-icon" />
        </button>
        <button
          type="button"
          title={`Next Walkthrough Stop${keyHint(CommandIds.nextWalkthroughStop)}`}
          aria-label="Next Stop"
          disabled={props.current >= props.stops.length - 1}
          onClick={() => props.go(props.current + 1)}
        >
          <ChevronRight class="ui-icon" />
        </button>
      </span>
      <For each={props.stops}>
        {(stop, index) => (
          <button
            type="button"
            class="ui-row walkthrough-strip-stop"
            classList={{
              "is-current": index() === props.current,
              "is-read": props.read.includes(stop.id),
            }}
            title={stop.title}
            onClick={() => props.go(index())}
          >
            <span class="ui-row-name">
              <b>{index() + 1}</b> {stop.name}
            </span>
            <Show when={stop.effect}>
              {(effect) => <span class={`walkthrough-dot is-${effect()}`} />}
            </Show>
          </button>
        )}
      </For>
    </nav>
  );
}
