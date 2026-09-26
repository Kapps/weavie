import { createSignal, type JSX, onCleanup, onMount, Show } from "solid-js";
import { setContext } from "../commands/context";
import { liveKeyLabel } from "../commands/keys-live";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { notify } from "../notify/notify";
import { AcpRegistryList, acpRegistryFeature } from "./AcpRegistryList";
import { ModalShell } from "./ModalShell";

export function AcpRegistryModal(props: { backendId: string; onClose: () => void }): JSX.Element {
  const [error, setError] = createSignal<string | null>(null);

  const reload = async (): Promise<boolean> => {
    setError(null);
    try {
      await acpRegistryFeature(props.backendId).request("reload", {});
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
      <AcpRegistryList backendId={props.backendId} removable={true} />
    </ModalShell>
  );
}
