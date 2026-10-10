import { createEffect, createSignal, For, type JSX, on, Show } from "solid-js";
import { Dynamic } from "solid-js/web";
import { controlMenuOpen } from "../chrome/ControlMenu";
import { ModalShell } from "../chrome/ModalShell";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { notify } from "../notify/notify";
import { AgentStep } from "./AgentStep";
import {
  COMPLETED_SETTING,
  gettingStartedOpen,
  gettingStartedStep,
  openGettingStarted,
  setGettingStartedOpen,
  writeSetting,
} from "./state";
import { FinishStep, InferenceStep, Keycaps, type SetupRun, ThemeStep } from "./steps";
import "./getting-started.css";

const STEPS: { title: string; hint: string; body: (props: { run: SetupRun }) => JSX.Element }[] = [
  {
    title: "Choose a look",
    hint: "Weavie follows your system by default. Changes apply instantly.",
    body: ThemeStep,
  },
  {
    title: "Pick your agent",
    hint: "New sessions start with this agent. You can pick another for any session.",
    body: AgentStep,
  },
  {
    title: "Smart suggestions",
    hint: "Small helpers from your agent. Turn them off anytime.",
    body: InferenceStep,
  },
  {
    title: "You're ready",
    hint: "One last tip, and a few keys worth learning.",
    body: FinishStep,
  },
];

registerCommand(CommandIds.configureSuggestions, () =>
  openGettingStarted(STEPS.findIndex((step) => step.body === InferenceStep)),
);

const INTERACTIVE = "button, select, input, textarea, a";

/** The setup steps; every choice saves immediately, and finishing (or skipping) marks setup done. */
export function GettingStarted(props: { onDone: () => void; escapeSkips: boolean }): JSX.Element {
  const [index, setIndex] = createSignal(gettingStartedStep());
  const [error, setError] = createSignal<string | null>(null);
  let section: HTMLElement | undefined;
  const attempt: SetupRun["attempt"] = (action) => {
    setError(null);
    action().catch((caught: unknown) =>
      setError(caught instanceof Error ? caught.message : String(caught)),
    );
  };
  const run: SetupRun = { attempt, agentChoice: 0, suggestionsAgent: null };

  const finish = () =>
    attempt(async () => {
      await writeSetting(COMPLETED_SETTING, true);
      props.onDone();
    });
  const step = () => STEPS[index()]!;
  const last = () => index() === STEPS.length - 1;
  const next = () => (last() ? finish() : setIndex(index() + 1));
  // Each step starts with focus on the page itself, so Enter advances and Tab reaches the first control.
  createEffect(on(index, () => section?.focus()));

  return (
    <section
      ref={section}
      class="getting-started"
      tabindex="-1"
      aria-labelledby="getting-started-title"
      onKeyDown={(event) => {
        if (event.key === "Enter" && !(event.target as Element).closest(INTERACTIVE)) {
          event.preventDefault();
          next();
        }
      }}
    >
      <div class="gs-progress">
        <For each={STEPS}>
          {(candidate, position) => (
            <button
              type="button"
              class="gs-segment"
              classList={{ "gs-reached": position() <= index() }}
              aria-current={position() === index() ? "step" : undefined}
              aria-label={`Step ${position() + 1}: ${candidate.title}`}
              title={candidate.title}
              onClick={() => setIndex(position())}
            />
          )}
        </For>
      </div>
      <p class="gs-eyebrow">
        Setup · Step {index() + 1} of {STEPS.length}
      </p>
      <h2 id="getting-started-title">{step().title}</h2>
      <p class="gs-hint">{step().hint}</p>
      <div class="gs-body">
        <Dynamic component={step().body} run={run} />
      </div>
      <Show when={error()}>{(message) => <p class="gs-error">{message()}</p>}</Show>
      <div class="gs-nav">
        <button type="button" class="gs-skip" onClick={finish}>
          Skip setup
          <Show when={props.escapeSkips}>
            <Keycaps label="Esc" />
          </Show>
        </button>
        <Show when={index() > 0}>
          <button type="button" class="gs-secondary" onClick={() => setIndex(index() - 1)}>
            Back
          </button>
        </Show>
        <button type="button" class="gs-primary" onClick={next}>
          {last() ? "Start using Weavie" : "Next"}
          <Keycaps label="↵" />
        </button>
      </div>
    </section>
  );
}

/** Getting Started over the workspace; closing it by any route counts as done (it's re-runnable). */
export function GettingStartedModal(): JSX.Element {
  const close = () => setGettingStartedOpen(false);
  const dismiss = () => {
    close();
    writeSetting(COMPLETED_SETTING, true).catch((caught: unknown) =>
      notify("error", `Couldn't save that setup is done: ${String(caught)}`),
    );
  };
  return (
    <Show when={gettingStartedOpen()}>
      <ModalShell
        labelledBy="getting-started-title"
        class="getting-started-dialog"
        onDismiss={dismiss}
        onKeyDown={(event) => {
          if (event.key === "Escape" && !controlMenuOpen()) {
            event.preventDefault();
            dismiss();
          }
        }}
      >
        <GettingStarted onDone={close} escapeSkips={true} />
      </ModalShell>
    </Show>
  );
}
