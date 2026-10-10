import { ChevronRight, MessageCircle } from "lucide-solid";
import { createResource, createSignal, For, type JSX, onMount, Show } from "solid-js";
import { LOCAL_BACKEND_ID, type ThemeMode } from "../bridge";
import { defaultAgentProvider } from "../chrome/agent-default";
import { liveKeyLabel } from "../commands/keys-live";
import { findCommandInCatalog } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { readSetting, writeSetting } from "../host-settings";
import { inferenceControls, openInferenceControls } from "../inference/inference-controls";
import { SuggestionSettings } from "../inference/SuggestionSettings";
import { chromeVars } from "../theme/chrome-vars";
import { savedAppearance, savedPalette } from "../theme/controller";
import { type ThemeChoice, themeRequest } from "../theme/picker-state";
import { browseThemes } from "./state";

/** What the steps of one setup run share: its error line, and choices that must outlive a step's view. */
export interface SetupRun {
  /** Runs a setup action, routing its failure to the page's error line. */
  attempt: (action: () => Promise<void>) => void;
  /** Bumped by every agent choice, so a slow install only takes effect if nothing was chosen since. */
  agentChoice: number;
  /** The agent suggestions were last pointed at, so revisiting the step keeps a "Which agent" change. */
  suggestionsAgent: string | null;
}

const MODES: { mode: ThemeMode; label: string }[] = [
  { mode: "system", label: "Match system" },
  { mode: "light", label: "Light" },
  { mode: "dark", label: "Dark" },
];

// A miniature window painted in one saved theme's own chrome colors.
function ThemeMock(props: { type: "light" | "dark"; class: string }): JSX.Element {
  return (
    <span class={`gs-mock ${props.class}`} style={chromeVars(savedPalette(props.type))}>
      <span class="gs-mock-bar" />
      <span class="gs-mock-side" />
      <span class="gs-mock-lines">
        <i style={{ width: "62%" }} />
        <i class="gs-mock-accent" style={{ width: "38%" }} />
        <i style={{ width: "74%" }} />
        <i style={{ width: "46%" }} />
      </span>
    </span>
  );
}

export function ThemeStep(props: { run: SetupRun }): JSX.Element {
  const [themes] = createResource(() =>
    themeRequest<ThemeChoice[]>("list", {}, new AbortController().signal),
  );
  const label = (type: "light" | "dark") => {
    const id = savedAppearance()[type];
    return themes()?.find((theme) => theme.id === id)?.label ?? id;
  };
  return (
    <>
      <fieldset class="gs-modes" aria-label="Color scheme">
        <For each={MODES}>
          {(option) => (
            <button
              type="button"
              class="gs-mode"
              aria-pressed={savedAppearance().mode === option.mode}
              onClick={() => props.run.attempt(() => writeSetting("theme.mode", option.mode))}
            >
              <span class="gs-mode-preview">
                <Show
                  when={option.mode === "system"}
                  fallback={
                    <ThemeMock type={option.mode === "light" ? "light" : "dark"} class="" />
                  }
                >
                  <ThemeMock type="light" class="" />
                  <ThemeMock type="dark" class="gs-mock-half" />
                </Show>
              </span>
              <span class="gs-mode-label">{option.label}</span>
            </button>
          )}
        </For>
      </fieldset>
      <p class="gs-themes">
        <span>
          Using <strong>{label("light")}</strong> and <strong>{label("dark")}</strong>.
        </span>
        <button type="button" class="gs-link" onClick={browseThemes}>
          Browse more themes
          <ChevronRight size="1em" aria-hidden="true" />
        </button>
      </p>
      <p class="gs-later">
        Change it anytime with <CommandName id={CommandIds.selectTheme} />
        <Keycaps label={liveKeyLabel(CommandIds.selectTheme)} />
      </p>
    </>
  );
}

export function InferenceStep(props: { run: SetupRun }): JSX.Element {
  const [ready, setReady] = createSignal(false);
  // Suggestions start on for a new user, and follow the agent picked on the previous step once per agent, so a
  // later agent choice here survives going back and forth.
  const initial = async (key: string) => {
    const setting = await readSetting<boolean>(key);
    if (setting.isDefault && !setting.value) await writeSetting(key, true);
  };
  onMount(() =>
    props.run.attempt(async () => {
      await initial("inference.enabled");
      await initial("inference.allowAutomatic");
      const agent = defaultAgentProvider(LOCAL_BACKEND_ID);
      const current = (await readSetting<string>("inference.defaultProvider")).value;
      if (props.run.suggestionsAgent !== agent && current !== agent) {
        await writeSetting("inference.defaultProvider", agent);
      }
      props.run.suggestionsAgent = agent;
      await openInferenceControls();
      setReady(true);
    }),
  );
  return (
    <Show when={ready() && inferenceControls()}>
      {(state) => (
        <>
          <SuggestionSettings state={state()} attempt={props.run.attempt} />
          <p class="gs-later">
            Change it anytime with <CommandName id={CommandIds.configureSuggestions} />
            <Keycaps label={liveKeyLabel(CommandIds.configureSuggestions)} />
          </p>
        </>
      )}
    </Show>
  );
}

const KEY_COMMANDS: { id: string; detail: string }[] = [
  { id: CommandIds.focusOmnibarCommands, detail: "Find any action" },
  { id: CommandIds.focusOmnibarFiles, detail: "Open a file" },
  { id: CommandIds.showSessions, detail: "Start or switch sessions" },
  { id: CommandIds.reviseSelection, detail: "Ask the agent to rewrite selected code" },
];

/** A shortcut label (e.g. `Ctrl+Shift+P`) drawn as one keycap per key. */
export function Keycaps(props: { label: string }): JSX.Element {
  return (
    <span class="gs-keycaps">
      <For each={props.label.split("+")}>{(key) => <kbd>{key}</kbd>}</For>
    </span>
  );
}

// A command's title from the live catalog, without a trailing ellipsis.
function CommandName(props: { id: string }): JSX.Element {
  return (
    <strong>{findCommandInCatalog(LOCAL_BACKEND_ID, props.id)?.title.replace(/…$/, "")}</strong>
  );
}

export function FinishStep(): JSX.Element {
  return (
    <>
      <div class="gs-ask">
        <MessageCircle size="1.4em" aria-hidden="true" />
        <p>
          <strong>Just ask your agent.</strong> Not sure how to do something in Weavie? Ask your
          agent. It can change settings, run commands, and explain features for you.
        </p>
      </div>
      <p class="gs-subhead">A few keys to start with</p>
      <ul class="gs-keys">
        <For each={KEY_COMMANDS}>
          {(command) => (
            <li>
              <span class="gs-text">
                <CommandName id={command.id} />
                <small>{command.detail}</small>
              </span>
              <span class="gs-combo">
                <Keycaps label={liveKeyLabel(command.id)} />
                <Show when={command.id === CommandIds.focusOmnibarFiles}>
                  <small>or</small>
                  <Keycaps label="Shift+Shift" />
                </Show>
              </span>
            </li>
          )}
        </For>
      </ul>
    </>
  );
}
