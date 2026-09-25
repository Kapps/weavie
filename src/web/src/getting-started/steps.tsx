import { createResource, createSignal, For, type JSX, Show } from "solid-js";
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
import { savedAppearance } from "../theme/controller";
import { selectTheme, type ThemeChoice, themeRequest } from "../theme/picker-state";
import { readSetting, writeSetting } from "./state";

/** Runs a setup action, routing its failure to the page's error line. */
export type Attempt = (action: () => Promise<void>) => void;

const MODES: { mode: ThemeMode; label: string }[] = [
  { mode: "system", label: "Match system" },
  { mode: "light", label: "Light" },
  { mode: "dark", label: "Dark" },
];

export function ThemeStep(props: { attempt: Attempt }): JSX.Element {
  const [themes] = createResource(() =>
    themeRequest<ThemeChoice[]>("list", {}, new AbortController().signal),
  );
  const themeSelect = (type: "light" | "dark", label: string) => (
    <label class="getting-started-field">
      {label}
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
      <fieldset class="getting-started-options" aria-label="Color scheme">
        <For each={MODES}>
          {(option) => (
            <button
              type="button"
              class="getting-started-option"
              aria-pressed={savedAppearance().mode === option.mode}
              onClick={() => props.attempt(() => writeSetting("theme.mode", option.mode))}
            >
              {option.label}
            </button>
          )}
        </For>
      </fieldset>
      {themeSelect("light", "Light theme")}
      {themeSelect("dark", "Dark theme")}
    </>
  );
}

export function AgentStep(props: { attempt: Attempt }): JSX.Element {
  const [browsing, setBrowsing] = createSignal(false);
  return (
    <>
      <fieldset class="getting-started-options getting-started-stack" aria-label="Agent">
        <For each={agentProviders(LOCAL_BACKEND_ID)}>
          {(provider) => (
            <button
              type="button"
              class="getting-started-option"
              aria-pressed={defaultAgentProvider(LOCAL_BACKEND_ID) === provider.id}
              disabled={!provider.available}
              onClick={() =>
                props.attempt(() => setDefaultAgentProvider(LOCAL_BACKEND_ID, provider.id))
              }
            >
              <strong>{provider.name}</strong>
              <small>
                {provider.available
                  ? provider.surface === "terminal"
                    ? "Runs in its own terminal UI"
                    : "Runs in Weavie's agent pane"
                  : provider.unavailableReason}
              </small>
            </button>
          )}
        </For>
      </fieldset>
      <div class="getting-started-row">
        <button
          type="button"
          onClick={() => props.attempt(() => refreshAgentProviders(LOCAL_BACKEND_ID))}
        >
          Check again
        </button>
        <button type="button" onClick={() => setBrowsing(!browsing())}>
          {browsing() ? "Hide ACP agents" : "Install an ACP agent…"}
        </button>
      </div>
      <Show when={browsing()}>
        <AcpRegistryList backendId={LOCAL_BACKEND_ID} removable={false} />
      </Show>
    </>
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
  return (
    <>
      <label class="getting-started-check">
        <input
          type="checkbox"
          checked={enabled() === true}
          disabled={enabled.loading}
          onChange={(event) => write("inference.enabled", event.currentTarget.checked, setEnabled)}
        />
        Let Weavie ask the model for small suggestions, such as branch names
      </label>
      <label class="getting-started-check">
        <input
          type="checkbox"
          checked={automatic() === true}
          disabled={enabled() !== true || automatic.loading}
          onChange={(event) =>
            write("inference.allowAutomatic", event.currentTarget.checked, setAutomatic)
          }
        />
        Allow those suggestions automatically, without a click each time (may use tokens)
      </label>
      <label class="getting-started-field">
        Inference provider
        <select
          disabled={enabled() !== true}
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
      </label>
    </>
  );
}

const KEY_COMMANDS = [
  CommandIds.focusOmnibarCommands,
  CommandIds.focusOmnibarFiles,
  CommandIds.showSessions,
  CommandIds.reviseSelection,
  CommandIds.findInFiles,
];

export function KeysStep(): JSX.Element {
  return (
    <dl class="getting-started-keys">
      <For each={KEY_COMMANDS}>
        {(id) => (
          <>
            <dt>{findCommandInCatalog(LOCAL_BACKEND_ID, id)?.title ?? id}</dt>
            <dd>
              <kbd>{liveKeyLabel(id) || "Unbound"}</kbd>
            </dd>
          </>
        )}
      </For>
    </dl>
  );
}
