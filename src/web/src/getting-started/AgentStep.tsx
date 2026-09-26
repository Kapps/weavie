import { ChevronLeft, ChevronRight } from "lucide-solid";
import { createResource, createSignal, For, type JSX, Show } from "solid-js";
import { LOCAL_BACKEND_ID } from "../bridge";
import {
  type AcpRegistryAgent,
  AcpRegistryList,
  acpRegistryFeature,
  installAcpAgent,
} from "../chrome/AcpRegistryList";
import {
  agentProviders,
  defaultAgentProvider,
  refreshAgentProviders,
  setDefaultAgentProvider,
} from "../chrome/agent-default";
import { writeSetting } from "./state";
import type { Attempt } from "./steps";

// Registry agents offered even before they're installed; everything shown about them comes from the live registry.
const SUGGESTED_AGENTS = ["claude-acp", "codex-acp"];

const ACP_TOOLTIP =
  "Made by a third party and maintained outside Weavie. It chats in Weavie's agent panel.";

function AcpTag(): JSX.Element {
  return (
    <span class="gs-tag" title={ACP_TOOLTIP}>
      ACP
    </span>
  );
}

export function AgentStep(props: { attempt: Attempt }): JSX.Element {
  const [browsing, setBrowsing] = createSignal(false);
  const [installing, setInstalling] = createSignal<string | null>(null);
  const [registry] = createResource(() =>
    acpRegistryFeature(LOCAL_BACKEND_ID).request<AcpRegistryAgent[]>("list", {}),
  );
  const installed = () => new Set(agentProviders(LOCAL_BACKEND_ID).map((provider) => provider.id));
  const suggested = () =>
    (registry() ?? []).filter(
      (agent) => SUGGESTED_AGENTS.includes(agent.id) && !installed().has(agent.id),
    );
  // Suggestions follow the default agent; the next step can still point them elsewhere.
  const use = async (id: string) => {
    await setDefaultAgentProvider(LOCAL_BACKEND_ID, id);
    await writeSetting("inference.defaultProvider", id);
  };
  const installAndUse = (agent: AcpRegistryAgent, distribution: string) =>
    props.attempt(async () => {
      setInstalling(agent.id);
      try {
        await installAcpAgent(LOCAL_BACKEND_ID, agent.id, distribution);
        await use(agent.id);
      } finally {
        setInstalling(null);
      }
    });
  const registryDescription = (id: string) =>
    registry.error ? undefined : registry()?.find((agent) => agent.id === id)?.description;

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
              disabled={!provider.available || installing() !== null}
              onClick={() => props.attempt(() => use(provider.id))}
            >
              <span class="gs-radio" aria-hidden="true" />
              <span class="gs-text">
                <strong>
                  {provider.name}
                  <Show when={provider.surface === "structured"}>
                    <AcpTag />
                  </Show>
                </strong>
                <small>
                  {provider.unavailableReason ??
                    (provider.surface === "terminal"
                      ? "Runs in a terminal inside Weavie"
                      : (registryDescription(provider.id) ?? "Chats in Weavie's agent panel"))}
                </small>
                <Show when={provider.warning}>
                  {(warning) => <small class="gs-warning">{warning()}</small>}
                </Show>
              </span>
            </button>
          )}
        </For>
        <For each={suggested()}>
          {(agent) => (
            <button
              type="button"
              class="gs-choice"
              aria-pressed={false}
              disabled={installing() !== null || agent.distributions[0] === undefined}
              onClick={() => installAndUse(agent, agent.distributions[0]!)}
            >
              <span class="gs-radio" aria-hidden="true" />
              <span class="gs-text">
                <strong>
                  {agent.name}
                  <AcpTag />
                </strong>
                <small>{agent.description}</small>
                <small class="gs-note">
                  {installing() === agent.id
                    ? "Installing… The first download can take a minute."
                    : agent.distributions[0] === undefined
                      ? "Not available to install right now."
                      : "Not installed yet. Choosing it installs it."}
                </small>
              </span>
            </button>
          )}
        </For>
      </fieldset>
      <Show when={registry.error}>
        {(error) => <small class="gs-warning">Couldn't load more agents: {String(error())}</small>}
      </Show>
      <div class="gs-links">
        <button type="button" class="gs-link" onClick={() => setBrowsing(true)}>
          Browse more agents
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
