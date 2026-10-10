import { ChevronRight, MessageCircle } from "lucide-solid";
import {
  createResource,
  createSignal,
  createUniqueId,
  For,
  type JSX,
  onMount,
  Show,
} from "solid-js";
import { LOCAL_BACKEND_ID, type ThemeMode } from "../bridge";
import { defaultAgentProvider } from "../chrome/agent-default";
import { liveKeyLabel } from "../commands/keys-live";
import { findCommandInCatalog } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { chromeVars } from "../theme/chrome-vars";
import { savedAppearance, savedPalette } from "../theme/controller";
import { type ThemeChoice, themeRequest } from "../theme/picker-state";
import { SuggestionProfile } from "./SuggestionProfile";
import { browseThemes, readSetting, writeSetting } from "./state";

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

// One labelled setting; the row's title and detail name its control.
function SettingRow(props: {
  title: string;
  detail: string;
  disabled: boolean;
  control: (id: string) => JSX.Element;
}): JSX.Element {
  const id = createUniqueId();
  return (
    <div class="gs-row" classList={{ "gs-disabled": props.disabled }}>
      <label class="gs-text" for={id}>
        <strong>{props.title}</strong>
        <small>{props.detail}</small>
      </label>
      {props.control(id)}
    </div>
  );
}

function Switch(props: {
  id: string;
  checked: boolean;
  disabled: boolean;
  onChange: (checked: boolean) => void;
}): JSX.Element {
  return (
    <input
      id={props.id}
      type="checkbox"
      role="switch"
      class="gs-switch"
      aria-checked={props.checked}
      checked={props.checked}
      disabled={props.disabled}
      onChange={(event) => props.onChange(event.currentTarget.checked)}
    />
  );
}

export function InferenceStep(props: { run: SetupRun }): JSX.Element {
  const [enabled, setEnabled] = createSignal<boolean>();
  const [automatic, setAutomatic] = createSignal<boolean>();
  const [provider, setProvider] = createSignal<string>();
  // Shows the new value at once; if the host refuses it (e.g. an env override), shows what the host actually has.
  const write = <T,>(key: string, value: T, set: (value: T) => void) =>
    props.run.attempt(async () => {
      set(value);
      try {
        await writeSetting(key, value);
      } catch (error) {
        set((await readSetting<T>(key)).value);
        throw error;
      }
    });
  // Switches never set start on. Suggestions follow an agent picked on the previous step in this run, once per agent,
  // so a choice made here survives going back and forth, and reopening the step never undoes it.
  const initial = async <T,>(key: string, onByDefault: T, set: (value: T) => void) => {
    const setting = await readSetting<T>(key);
    const value = setting.isDefault ? onByDefault : setting.value;
    if (value !== setting.value) await writeSetting(key, value);
    set(value);
  };
  onMount(() =>
    props.run.attempt(async () => {
      await initial("inference.enabled", true, setEnabled);
      await initial("inference.allowAutomatic", true, setAutomatic);
      const agent = defaultAgentProvider(LOCAL_BACKEND_ID);
      const current = (await readSetting<string>("inference.defaultProvider")).value;
      if (
        props.run.agentChoice === 0 ||
        props.run.suggestionsAgent === agent ||
        current === agent
      ) {
        setProvider(current);
      } else {
        await writeSetting("inference.defaultProvider", agent);
        setProvider(agent);
      }
      props.run.suggestionsAgent = agent;
    }),
  );
  const off = () => enabled() !== true;
  return (
    <>
      <SettingRow
        title="Allow suggestions"
        detail="Your agent helps with small things, like naming a branch. Never shown in your chat."
        disabled={enabled() === undefined}
        control={(id) => (
          <Switch
            id={id}
            checked={enabled() === true}
            disabled={enabled() === undefined}
            onChange={(checked) => write("inference.enabled", checked, setEnabled)}
          />
        )}
      />
      <SettingRow
        title="Suggest automatically"
        detail="Suggest without being asked. This uses a little of your agent usage now and then."
        disabled={off()}
        control={(id) => (
          <Switch
            id={id}
            checked={automatic() === true}
            disabled={off() || automatic() === undefined}
            onChange={(checked) => write("inference.allowAutomatic", checked, setAutomatic)}
          />
        )}
      />
      <Show when={provider()}>
        <SuggestionProfile disabled={off()} attempt={props.run.attempt} />
      </Show>
      <p class="gs-later">
        Change it anytime with <CommandName id={CommandIds.configureSuggestions} />
        <Show when={liveKeyLabel(CommandIds.configureSuggestions)}>
          {(keys) => <Keycaps label={keys()} />}
        </Show>
      </p>
    </>
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
