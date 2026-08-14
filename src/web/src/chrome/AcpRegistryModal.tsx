import { createSignal, For, type JSX, onCleanup, onMount, Show } from "solid-js";
import { hostConnection } from "../bridge";
import { setContext } from "../commands/context";
import { liveKeyLabel } from "../commands/keys-live";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { notify } from "../notify/notify";
import { ModalShell } from "./ModalShell";

interface AcpRegistryAgent {
  id: string;
  name: string;
  version: string;
  description: string;
  distributions: string[];
  installedDistribution: string | null;
  installedVersion: string | null;
}

export function AcpRegistryModal(props: { backendId: string; onClose: () => void }): JSX.Element {
  const [agents, setAgents] = createSignal<AcpRegistryAgent[]>([]);
  const [selected, setSelected] = createSignal<Record<string, string>>({});
  const [loading, setLoading] = createSignal(true);
  const [busy, setBusy] = createSignal<string | null>(null);
  const [error, setError] = createSignal<string | null>(null);

  const feature = () => {
    const connection = hostConnection(props.backendId);
    if (connection === undefined) throw new Error("The selected Weavie host is not connected.");
    return connection.host.feature("acpRegistry");
  };

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
      await feature().request("install", { id: agent.id, distribution });
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

  const reload = async (): Promise<boolean> => {
    setError(null);
    try {
      await feature().request("reload", {});
      notify("info", "ACP agent definitions were reloaded.");
      return true;
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : String(caught));
      return false;
    }
  };

  onMount(() => {
    setContext("acpRegistryOpen", true);
    const offReload = registerCommand(CommandIds.reloadAcpAgents, () => void reload());
    void load();
    onCleanup(() => {
      offReload();
      setContext("acpRegistryOpen", false);
    });
  });

  return (
    <ModalShell
      labelledBy="acp-registry-title"
      onDismiss={props.onClose}
      onKeyDown={(event) => {
        if (event.key === "Escape") {
          event.preventDefault();
          props.onClose();
        }
      }}
      class="acp-registry-dialog"
    >
      <div class="acp-registry-heading">
        <div>
          <div class="confirm-title" id="acp-registry-title">
            ACP agents
          </div>
          <div class="confirm-body">
            Install agents from the official Agent Client Protocol registry.
          </div>
        </div>
        <button
          type="button"
          onClick={() => void reload()}
          title={`Reload custom agents${liveKeyLabel(CommandIds.reloadAcpAgents) === "" ? "" : ` (${liveKeyLabel(CommandIds.reloadAcpAgents)})`}`}
        >
          Reload
        </button>
        <button type="button" onClick={props.onClose} title="Close (Esc)" aria-label="Close">
          ×
        </button>
      </div>
      <Show when={error()}>{(message) => <div class="session-prompt-error">{message()}</div>}</Show>
      <Show when={!loading()} fallback={<div class="acp-registry-state">Loading registry…</div>}>
        <div class="acp-registry-list">
          <For
            each={agents()}
            fallback={<div class="acp-registry-state">No agents are available.</div>}
          >
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
                    <Show when={installed()}>
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
    </ModalShell>
  );
}
