import { AlertTriangle } from "lucide-solid";
import { createSignal, type JSX, onMount, Show } from "solid-js";
import type { InferenceChoices } from "../bridge";
import { ControlBar } from "../chrome/ControlBar";
import { localFeature, writeSetting } from "./state";
import type { SetupRun } from "./steps";

/** What suggestions run on: agent, model, effort, and Fast Mode pickers whose options the agent itself reports. */
export function SuggestionProfile(props: {
  disabled: boolean;
  attempt: SetupRun["attempt"];
}): JSX.Element {
  const [choices, setChoices] = createSignal<InferenceChoices>();
  const [loading, setLoading] = createSignal(true);
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
    props.attempt(async () => {
      await writeSetting(key, value);
      await ask();
    });
  };
  return (
    <section class="gs-row gs-profile" classList={{ "gs-disabled": props.disabled }}>
      <div class="gs-text">
        <strong>Suggestion model</strong>
        <small>Choose the agent and model for suggestions.</small>
      </div>
      <ControlBar
        label="Suggestion agent and model"
        axes={choices()?.axes}
        placeholders={["Agent", "Model", "Effort"]}
        disabled={props.disabled}
        busy={loading()}
        menuClass="gs-menu"
        onPick={pick}
      />
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
    </section>
  );
}
