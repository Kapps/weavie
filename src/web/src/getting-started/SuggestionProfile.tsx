import { AlertTriangle, ChevronDown } from "lucide-solid";
import { createSignal, For, type JSX, onMount, Show } from "solid-js";
import { Portal } from "solid-js/web";
import type { AgentControlAxis, InferenceChoices } from "../bridge";
import { ControlMenu } from "../chrome/ControlMenu";
import { localFeature, writeSetting } from "./state";
import type { SetupRun } from "./steps";

/** What suggestions run on: agent, model, effort, and Fast Mode pickers whose options the agent itself reports. */
export function SuggestionProfile(props: {
  disabled: boolean;
  attempt: SetupRun["attempt"];
}): JSX.Element {
  const [choices, setChoices] = createSignal<InferenceChoices>();
  const [loading, setLoading] = createSignal(true);
  const [open, setOpen] = createSignal<{ axis: AgentControlAxis; anchor: DOMRect } | null>(null);
  let asked = 0;
  const ask = async () => {
    const turn = ++asked;
    setLoading(true);
    try {
      const next = await localFeature("inferenceControls").request<InferenceChoices>("get", {});
      if (turn === asked) setChoices(next);
    } finally {
      if (turn === asked) setLoading(false);
    }
  };
  onMount(() => props.attempt(ask));
  const pick = (key: string, value: string) => {
    setOpen(null);
    props.attempt(async () => {
      await writeSetting(key, value);
      await ask();
    });
  };
  return (
    <section class="gs-profile" classList={{ "gs-disabled": props.disabled }}>
      <p class="gs-profile-head">
        <strong>Runs on</strong>
        <small>Separate from your chat, so it never changes the model you chat with.</small>
      </p>
      <fieldset class="gs-bar" aria-label="Suggestion agent and model" disabled={props.disabled}>
        <For each={choices()?.axes}>
          {(axis) => (
            <button
              type="button"
              class="gs-pick"
              disabled={loading()}
              aria-haspopup="listbox"
              aria-expanded={open()?.axis.id === axis.id}
              title={`${axis.label}: ${axis.valueLabel}`}
              onClick={(event) => toggle(axis, event.currentTarget)}
              onKeyDown={(event) => {
                if (event.key === "ArrowDown" && open() === null) {
                  event.preventDefault();
                  toggle(axis, event.currentTarget);
                }
              }}
            >
              <small>{axis.label}</small>
              <span>
                {axis.valueLabel}
                <ChevronDown size="0.95em" aria-hidden="true" />
              </span>
            </button>
          )}
        </For>
        <Show when={choices() === undefined}>
          <For each={["Agent", "Model", "Effort"]}>
            {(label) => (
              <span
                class="gs-pick gs-placeholder"
                aria-busy="true"
                title={`${label}: asking the agent…`}
              >
                <small>{label}</small>
                <i />
              </span>
            )}
          </For>
        </Show>
      </fieldset>
      <Show when={choices()?.error ?? choices()?.warning}>
        {(problem) => (
          <p class="gs-profile-problem" role="alert">
            <AlertTriangle size="1em" aria-hidden="true" />
            <span>{choices()?.error ? `Couldn't get the options: ${problem()}` : problem()}</span>
            <Show when={choices()?.error}>
              <button type="button" class="gs-link" onClick={() => props.attempt(ask)}>
                Try again
              </button>
            </Show>
          </p>
        )}
      </Show>
      <Show when={open()} keyed>
        {(menu) => (
          <Portal>
            <ControlMenu
              axis={menu.axis}
              class="gs-menu"
              style={placement(menu.anchor)}
              inside=".gs-menu, .gs-pick"
              onPick={(value) => pick(menu.axis.id, value)}
              onClose={() => setOpen(null)}
            />
          </Portal>
        )}
      </Show>
    </section>
  );

  function toggle(axis: AgentControlAxis, segment: HTMLElement): void {
    setOpen((current) =>
      current?.axis.id === axis.id ? null : { axis, anchor: segment.getBoundingClientRect() },
    );
  }
}

// Below the segment, or above it when the viewport has no room underneath.
function placement(anchor: DOMRect): JSX.CSSProperties {
  const below = window.innerHeight - anchor.bottom;
  const above = below < 180 && anchor.top > below;
  return {
    position: "fixed",
    left: `${anchor.left}px`,
    "min-width": `${Math.max(anchor.width, 200)}px`,
    "max-height": `${Math.min((above ? anchor.top : below) - 12, window.innerHeight * 0.45)}px`,
    ...(above
      ? { bottom: `${window.innerHeight - anchor.top + 4}px` }
      : { top: `${anchor.bottom + 4}px` }),
  };
}
