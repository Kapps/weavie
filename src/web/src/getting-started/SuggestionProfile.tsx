import { AlertTriangle, ChevronDown } from "lucide-solid";
import { createSignal, For, type JSX, onMount, Show } from "solid-js";
import { Portal } from "solid-js/web";
import type { AgentControlAxis, InferenceChoices } from "../bridge";
import { ControlMenu } from "../chrome/ControlMenu";
import { localFeature, writeSetting } from "./state";
import type { SetupRun } from "./steps";

const AGENT = "inference.defaultProvider";

/** What suggestions run on: agent, model, effort, and Fast Mode pickers whose options the agent itself reports. */
export function SuggestionProfile(props: {
  disabled: boolean;
  attempt: SetupRun["attempt"];
}): JSX.Element {
  const [choices, setChoices] = createSignal<InferenceChoices>();
  // The setting just changed, while the agent is asked again; a new agent's old options aren't shown meanwhile.
  const [asking, setAsking] = createSignal<string | null>(AGENT);
  const [open, setOpen] = createSignal<{ axis: AgentControlAxis; anchor: DOMRect } | null>(null);
  let asked = 0;
  const ask = async (changed: string) => {
    const turn = ++asked;
    setAsking(changed);
    const next = await localFeature("inferenceControls").request<InferenceChoices>("get", {});
    if (turn === asked) {
      setChoices(next);
      setAsking(null);
    }
  };
  onMount(() => props.attempt(() => ask(AGENT)));
  const pick = (key: string, value: string) => {
    setOpen(null);
    props.attempt(async () => {
      await writeSetting(key, value);
      await ask(key);
    });
  };
  const axes = () =>
    asking() === AGENT
      ? (choices()?.axes.filter((axis) => axis.id === AGENT) ?? [])
      : (choices()?.axes ?? []);
  return (
    <section class="gs-profile" classList={{ "gs-disabled": props.disabled }}>
      <p class="gs-profile-head">
        <strong>Runs on</strong>
        <small>Separate from your chat, so it never changes the model you chat with.</small>
      </p>
      <fieldset class="gs-bar" aria-label="Suggestion agent and model" disabled={props.disabled}>
        <For each={axes()}>
          {(axis) => (
            <button
              type="button"
              class="gs-segment"
              classList={{ "gs-busy": asking() !== null && axis.id !== AGENT }}
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
        <Show when={asking() === AGENT}>
          <For each={choices() === undefined ? ["Agent", "Model", "Effort"] : ["Model", "Effort"]}>
            {(label) => (
              <span
                class="gs-segment gs-placeholder"
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
              <button type="button" class="gs-link" onClick={() => props.attempt(() => ask(AGENT))}>
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
              inside=".gs-menu, .gs-segment"
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
