import { createSignal, For, type JSX, Show } from "solid-js";
import { Dynamic } from "solid-js/web";
import { ModalShell } from "../chrome/ModalShell";
import { notify } from "../notify/notify";
import {
  COMPLETED_SETTING,
  gettingStartedOpen,
  setGettingStartedOpen,
  writeSetting,
} from "./state";
import { AgentStep, type Attempt, InferenceStep, KeysStep, ThemeStep } from "./steps";
import "./getting-started.css";

const STEPS: { title: string; hint: string; body: (props: { attempt: Attempt }) => JSX.Element }[] =
  [
    {
      title: "Appearance",
      hint: "Pick a color scheme. It applies as you choose.",
      body: ThemeStep,
    },
    {
      title: "Agent",
      hint: "Choose the agent new sessions start with, or install one from the ACP registry.",
      body: AgentStep,
    },
    {
      title: "AI suggestions",
      hint: "Weavie can make small, isolated model calls outside your session. They're off unless you allow them.",
      body: InferenceStep,
    },
    {
      title: "Keyboard",
      hint: "Weavie is built for the keyboard. These are the shortcuts to learn first.",
      body: KeysStep,
    },
  ];

/** The setup steps; every choice saves immediately, and finishing (or skipping) marks setup done. */
export function GettingStarted(props: { onDone: () => void }): JSX.Element {
  const [index, setIndex] = createSignal(0);
  const [error, setError] = createSignal<string | null>(null);
  const attempt: Attempt = (action) => {
    setError(null);
    action().catch((caught: unknown) =>
      setError(caught instanceof Error ? caught.message : String(caught)),
    );
  };
  const finish = () =>
    attempt(async () => {
      await writeSetting(COMPLETED_SETTING, true);
      props.onDone();
    });
  const step = () => STEPS[index()]!;
  const last = () => index() === STEPS.length - 1;

  return (
    <section class="getting-started" aria-labelledby="getting-started-title">
      <ol class="getting-started-progress">
        <For each={STEPS}>
          {(candidate, position) => (
            <li aria-current={position() === index() ? "step" : undefined}>
              <button type="button" onClick={() => setIndex(position())}>
                {candidate.title}
              </button>
            </li>
          )}
        </For>
      </ol>
      <h2 id="getting-started-title">{step().title}</h2>
      <p class="getting-started-hint">{step().hint}</p>
      <div class="getting-started-body">
        <Dynamic component={step().body} attempt={attempt} />
      </div>
      <Show when={error()}>{(message) => <p class="getting-started-error">{message()}</p>}</Show>
      <div class="getting-started-nav">
        <button type="button" class="getting-started-skip" onClick={finish}>
          Skip setup
        </button>
        <Show when={index() > 0}>
          <button type="button" onClick={() => setIndex(index() - 1)}>
            Back
          </button>
        </Show>
        <button
          type="button"
          class="getting-started-next"
          onClick={() => (last() ? finish() : setIndex(index() + 1))}
        >
          {last() ? "Finish" : "Next"}
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
          if (event.key === "Escape") {
            event.preventDefault();
            dismiss();
          }
        }}
      >
        <GettingStarted onDone={close} />
      </ModalShell>
    </Show>
  );
}
