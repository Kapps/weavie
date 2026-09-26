import { createSignal, For, type JSX, onMount, Show } from "solid-js";
import { hostConnection } from "../bridge";
import { notify } from "../notify/notify";

/** One agent in the official ACP registry, with what this host has installed of it. */
export interface AcpRegistryAgent {
  id: string;
  name: string;
  version: string;
  description: string;
  distributions: string[];
  installedDistribution: string | null;
  installedVersion: string | null;
}

// The official registry's agents with install / update (and, where the host allows it, remove) actions.
export function AcpRegistryList(props: { backendId: string; removable: boolean }): JSX.Element {
  const [agents, setAgents] = createSignal<AcpRegistryAgent[]>([]);
  const [selected, setSelected] = createSignal<Record<string, string>>({});
  const [loading, setLoading] = createSignal(true);
  const [busy, setBusy] = createSignal<string | null>(null);
  const [error, setError] = createSignal<string | null>(null);
  const [filter, setFilter] = createSignal("");
  const shown = () => {
    const query = filter().trim().toLowerCase();
    return agents().filter((agent) =>
      `${agent.name} ${agent.description}`.toLowerCase().includes(query),
    );
  };
  const feature = () => acpRegistryFeature(props.backendId);

  const load = async (): Promise<void> => {
    setLoading(true);
    setError(null);
    try {
      const result = await feature().request<AcpRegistryAgent[]>("list", {});
      setAgents(result);
      setSelected(
        Object.fromEntries(
          result.flatMap((agent) =>
            agent.distributions[0] === undefined ? [] : [[agent.id, agent.distributions[0]]],
          ),
        ),
      );
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : String(caught));
    } finally {
      setLoading(false);
    }
  };

  const install = async (agent: AcpRegistryAgent, distribution: string): Promise<void> => {
    setBusy(agent.id);
    setError(null);
    try {
      await installAcpAgent(props.backendId, agent.id, distribution);
      notify("info", `${agent.name} ${agent.version} is installed through ${distribution}.`);
      await load();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : String(caught));
    } finally {
      setBusy(null);
    }
  };

  const remove = async (agent: AcpRegistryAgent): Promise<void> => {
    setBusy(agent.id);
    setError(null);
    try {
      await feature().request("remove", { id: agent.id });
      notify("info", `${agent.name} was removed.`);
      await load();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : String(caught));
    } finally {
      setBusy(null);
    }
  };

  onMount(() => void load());

  return (
    <>
      <Show when={error()}>{(message) => <div class="session-prompt-error">{message()}</div>}</Show>
      <input
        type="search"
        class="acp-registry-filter"
        placeholder="Filter agents"
        aria-label="Filter agents"
        value={filter()}
        onInput={(event) => setFilter(event.currentTarget.value)}
      />
      <Show when={!loading()} fallback={<div class="acp-registry-state">Loading registry…</div>}>
        <div class="acp-registry-list">
          <For each={shown()} fallback={<div class="acp-registry-state">No agents match.</div>}>
            {(agent) => {
              const installed = () => agent.installedDistribution !== null;
              const current = () => agent.installedVersion === agent.version;
              const chosen = () =>
                installed() ? agent.installedDistribution! : (selected()[agent.id] ?? "");
              return (
                <article class="acp-registry-agent">
                  <div class="acp-registry-agent-copy">
                    <div class="acp-registry-agent-title">
                      <strong>{agent.name}</strong>
                      <span>{agent.version}</span>
                      <Show when={installed()}>
                        <span class="acp-registry-installed">
                          {current() ? "Installed" : `Installed ${agent.installedVersion}`}
                        </span>
                      </Show>
                    </div>
                    <p>{agent.description}</p>
                  </div>
                  <div class="acp-registry-agent-actions">
                    <Show when={!installed() && agent.distributions.length > 1}>
                      <select
                        aria-label={`Distribution for ${agent.name}`}
                        value={chosen()}
                        onChange={(event) =>
                          setSelected((value) => ({
                            ...value,
                            [agent.id]: event.currentTarget.value,
                          }))
                        }
                      >
                        <For each={agent.distributions}>
                          {(distribution) => <option value={distribution}>{distribution}</option>}
                        </For>
                      </select>
                    </Show>
                    <Show when={!installed() || !current()}>
                      <button
                        type="button"
                        disabled={busy() !== null || chosen() === ""}
                        onClick={() => void install(agent, chosen())}
                      >
                        {busy() === agent.id ? "Working…" : installed() ? "Update" : "Install"}
                      </button>
                    </Show>
                    <Show when={installed() && props.removable}>
                      <button
                        type="button"
                        disabled={busy() !== null}
                        onClick={() => void remove(agent)}
                      >
                        Remove
                      </button>
                    </Show>
                  </div>
                </article>
              );
            }}
          </For>
        </div>
      </Show>
    </>
  );
}

function connected(backendId: string) {
  const connection = hostConnection(backendId);
  if (connection === undefined) throw new Error("The selected Weavie host is not connected.");
  return connection;
}

export function acpRegistryFeature(backendId: string) {
  return connected(backendId).host.feature("acpRegistry");
}

/**
 * Installs a registry agent and resolves once the host has started it and it answered as an ACP agent (a first
 * npx/uvx start downloads it, so this can take a while); rejects with the agent's own error output otherwise.
 */
export function installAcpAgent(
  backendId: string,
  id: string,
  distribution: string,
): Promise<void> {
  const connection = connected(backendId);
  const feature = connection.host.feature("acpRegistry");
  return new Promise((resolve, reject) => {
    let replayingHello = connection.currentHello !== null;
    const stop = (): void => {
      offResult();
      offHello();
    };
    const offResult = feature.on<{ id: string; error: string | null }>("installed", (result) => {
      if (result.id !== id) return;
      stop();
      if (result.error === null) resolve();
      else reject(new Error(result.error));
    });
    // The result is announced once; a reconnect in between can miss it, so say so rather than wait forever.
    const offHello = connection.onHello(() => {
      if (replayingHello) {
        replayingHello = false;
        return;
      }
      stop();
      reject(
        new Error("The connection to Weavie dropped during the install. Check the agent list."),
      );
    });
    feature.request("install", { id, distribution }).catch((error: unknown) => {
      stop();
      reject(error);
    });
  });
}
