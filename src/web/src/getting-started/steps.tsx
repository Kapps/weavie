import { ChevronLeft, ChevronRight } from "lucide-solid";
import { createResource, createSignal, createUniqueId, For, type JSX, Show } from "solid-js";
import { LOCAL_BACKEND_ID, type ThemeMode } from "../bridge";
import { AcpRegistryList } from "../chrome/AcpRegistryList";
import {
  agentProviders,
  defaultAgentProvider,
  refreshAgentProviders,
  setDefaultAgentProvider,
} from "../chrome/agent-default";
import { liveKeyLabel } from "../commands/keys-live";
import { findCommandInCatalog } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { chromeVars } from "../theme/chrome-vars";
import { savedAppearance, savedPalette } from "../theme/controller";
import { selectTheme, type ThemeChoice, themeRequest } from "../theme/picker-state";
import { readSetting, writeSetting } from "./state";

/** Runs a setup action, routing its failure to the page's error line. */
export type Attempt = (action: () => Promise<void>) => void;

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

export function ThemeStep(props: { attempt: Attempt }): JSX.Element {
  const [themes] = createResource(() =>
    themeRequest<ThemeChoice[]>("list", {}, new AbortController().signal),
  );
  const themeSelect = (type: "light" | "dark", label: string) => (
    <label class="gs-field">
      <span>{label}</span>
      <select
        onChange={(event) => {
          const id = event.currentTarget.value;
          props.attempt(() => selectTheme(id, new AbortController().signal));
        }}
      >
        <For each={(themes() ?? []).filter((theme) => theme.type === type)}>
          {(theme) => (
            <option value={theme.id} selected={theme.id === savedAppearance()[type]}>
              {theme.label}
            </option>
          )}
        </For>
      </select>
    </label>
  );
  return (
    <>
      <fieldset class="gs-modes" aria-label="Color scheme">
        <For each={MODES}>
          {(option) => (
            <button
              type="button"
              class="gs-mode"
              aria-pressed={savedAppearance().mode === option.mode}
              onClick={() => props.attempt(() => writeSetting("theme.mode", option.mode))}
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
      <div class="gs-pair">
        {themeSelect("light", "Light theme")}
        {themeSelect("dark", "Dark theme")}
      </div>
    </>
  );
}

export function AgentStep(props: { attempt: Attempt }): JSX.Element {
  const [browsing, setBrowsing] = createSignal(false);
  // Suggestions follow the default agent; the next step can still point them elsewhere.
  const choose = (id: string) =>
    props.attempt(async () => {
      await setDefaultAgentProvider(LOCAL_BACKEND_ID, id);
      await writeSetting("inference.defaultProvider", id);
    });
  return (
    <Show
      when={!browsing()}
      fallback={
        <>
          <button type="button" class="gs-link" onClick={() => setBrowsing(false)}>
            <ChevronLeft size="1em" aria-hidden="true" />
            Back to your agents
          </button>
          <div class="gs-registry">
            <AcpRegistryList backendId={LOCAL_BACKEND_ID} removable={false} />
          </div>
        </>
      }
    >
      <fieldset class="gs-choices" aria-label="Agent">
        <For each={agentProviders(LOCAL_BACKEND_ID)}>
          {(provider) => (
            <button
              type="button"
              class="gs-choice"
              aria-pressed={defaultAgentProvider(LOCAL_BACKEND_ID) === provider.id}
              disabled={!provider.available}
              onClick={() => choose(provider.id)}
            >
              <span class="gs-radio" aria-hidden="true" />
              <span class="gs-text">
                <strong>{provider.name}</strong>
                <small>
                  {provider.unavailableReason ??
                    (provider.surface === "terminal"
                      ? "Its own terminal UI, embedded in a Weavie pane"
                      : "Runs in Weavie's native agent pane")}
                </small>
                <Show when={provider.warning}>
                  {(warning) => <small class="gs-warning">{warning()}</small>}
                </Show>
              </span>
            </button>
          )}
        </For>
      </fieldset>
      <div class="gs-links">
        <button type="button" class="gs-link" onClick={() => setBrowsing(true)}>
          Install another agent from the ACP registry
          <ChevronRight size="1em" aria-hidden="true" />
        </button>
        <Show when={agentProviders(LOCAL_BACKEND_ID).some((provider) => provider.warning !== null)}>
          <button
            type="button"
            class="gs-link"
            onClick={() => props.attempt(() => refreshAgentProviders(LOCAL_BACKEND_ID))}
          >
            Installed it? Check again
          </button>
        </Show>
      </div>
    </Show>
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

export function InferenceStep(props: { attempt: Attempt }): JSX.Element {
  const [enabled, { mutate: setEnabled }] = createResource(() =>
    readSetting<boolean>("inference.enabled"),
  );
  const [automatic, { mutate: setAutomatic }] = createResource(() =>
    readSetting<boolean>("inference.allowAutomatic"),
  );
  const [provider, { mutate: setProvider }] = createResource(() =>
    readSetting<string>("inference.defaultProvider"),
  );
  const write = <T,>(key: string, value: T, mutate: (value: T) => void) =>
    props.attempt(async () => {
      await writeSetting(key, value);
      mutate(value);
    });
  const off = () => enabled() !== true;
  return (
    <>
      <SettingRow
        title="Allow suggestions"
        detail="Weavie asks your agent for small things, like a branch name for a new session. These calls stay out of your conversation."
        disabled={enabled.loading}
        control={(id) => (
          <Switch
            id={id}
            checked={enabled() === true}
            disabled={enabled.loading}
            onChange={(checked) => write("inference.enabled", checked, setEnabled)}
          />
        )}
      />
      <SettingRow
        title="Suggest automatically"
        detail="Offer suggestions without waiting for a click. Uses a few tokens now and then."
        disabled={off()}
        control={(id) => (
          <Switch
            id={id}
            checked={automatic() === true}
            disabled={off() || automatic.loading}
            onChange={(checked) => write("inference.allowAutomatic", checked, setAutomatic)}
          />
        )}
      />
      <SettingRow
        title="Answered by"
        detail="The agent that handles these suggestions."
        disabled={off()}
        control={(id) => (
          <select
            id={id}
            disabled={off()}
            onChange={(event) =>
              write("inference.defaultProvider", event.currentTarget.value, setProvider)
            }
          >
            <For each={agentProviders(LOCAL_BACKEND_ID).filter((agent) => agent.available)}>
              {(agent) => (
                <option value={agent.id} selected={agent.id === provider()}>
                  {agent.name}
                </option>
              )}
            </For>
          </select>
        )}
      />
    </>
  );
}

const KEY_COMMANDS: { id: string; detail: string }[] = [
  { id: CommandIds.focusOmnibarCommands, detail: "Run any action by name" },
  { id: CommandIds.focusOmnibarFiles, detail: "Jump to a file" },
  { id: CommandIds.showSessions, detail: "Start or switch agent sessions" },
  { id: CommandIds.reviseSelection, detail: "Have the agent rewrite the selected code" },
  { id: CommandIds.findInFiles, detail: "Search the whole workspace" },
];

/** A shortcut label (e.g. `Ctrl+Shift+P`) drawn as one keycap per key. */
export function Keycaps(props: { label: string }): JSX.Element {
  return (
    <span class="gs-keycaps">
      <For each={props.label.split("+")}>{(key) => <kbd>{key}</kbd>}</For>
    </span>
  );
}

export function KeysStep(): JSX.Element {
  return (
    <>
      <ul class="gs-keys">
        <For each={KEY_COMMANDS}>
          {(command) => (
            <li>
              <span class="gs-text">
                <strong>{findCommandInCatalog(LOCAL_BACKEND_ID, command.id)?.title}</strong>
                <small>{command.detail}</small>
              </span>
              <Keycaps label={liveKeyLabel(command.id)} />
            </li>
          )}
        </For>
      </ul>
      <p class="gs-footnote">
        Hover any button to see its shortcut. You can rebind every one in keybindings.json.
      </p>
    </>
  );
}
