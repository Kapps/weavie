import { createSignal, type JSX } from "solid-js";
import { log } from "../bridge";
import { ModalShell, modalSubmitKeys, PromptActions, PromptButton } from "./ModalShell";
import { addAgent, type RemoteAgent } from "./remote-agents";

// Register a remote agent (name + runner control-plane URL/token). On save we persist and connect, so it
// appears as a New Session location. Esc cancels; Enter saves.
export function RegisterAgentModal(props: {
  onClose: () => void;
  onAdded: (name: string) => void;
}): JSX.Element {
  const [name, setName] = createSignal("");
  const [url, setUrl] = createSignal("");
  const [token, setToken] = createSignal("");
  const [busy, setBusy] = createSignal(false);
  const [error, setError] = createSignal<string | null>(null);

  const canSave = (): boolean =>
    name().trim() !== "" && url().trim() !== "" && token().trim() !== "" && !busy();

  const save = async (): Promise<void> => {
    if (busy()) {
      return;
    }
    // The primary button is disabled when fields are blank, but the Enter shortcut bypasses that — so say
    // what's missing rather than no-op silently.
    if (name().trim() === "" || url().trim() === "" || token().trim() === "") {
      setError("Enter a name, runner URL, and token.");
      return;
    }
    setBusy(true);
    setError(null);
    const agent: RemoteAgent = { name: name().trim(), url: url().trim(), token: token().trim() };
    try {
      await addAgent(agent);
      props.onAdded(agent.name);
      props.onClose();
    } catch (err) {
      const message = String(err);
      setError(message);
      log("error", `register remote agent failed: ${message}`);
    } finally {
      setBusy(false);
    }
  };

  const onKeyDown = modalSubmitKeys(() => void save(), props.onClose);
  return (
    <ModalShell
      labelledBy="register-agent-title"
      onDismiss={props.onClose}
      onKeyDown={onKeyDown}
      class="session-prompt"
    >
      <div class="confirm-title" id="register-agent-title">
        Add remote agent
      </div>
      <div class="confirm-body">
        Point Weavie at a remote runner. URL + token are printed in the runner's console at startup
        (reachable over Tailscale, e.g. http://your-host:8800).
      </div>
      <input
        class="session-prompt-input"
        type="text"
        placeholder="name (e.g. devbox)"
        spellcheck={false}
        autocomplete="off"
        value={name()}
        onInput={(e) => setName(e.currentTarget.value)}
        ref={(el) => queueMicrotask(() => el.focus())}
      />
      <input
        class="session-prompt-input"
        type="text"
        placeholder="runner URL (http://host:8800)"
        spellcheck={false}
        autocomplete="off"
        value={url()}
        onInput={(e) => setUrl(e.currentTarget.value)}
      />
      <input
        class="session-prompt-input"
        type="text"
        placeholder="runner token"
        spellcheck={false}
        autocomplete="off"
        value={token()}
        onInput={(e) => setToken(e.currentTarget.value)}
      />
      {error() !== null ? <div class="session-prompt-error">{error()}</div> : null}
      <PromptActions>
        <PromptButton label="Cancel" shortcut="Esc" title="Cancel (Esc)" onClick={props.onClose} />
        <PromptButton
          label={busy() ? "Connecting…" : "Add"}
          shortcut="Enter"
          title="Save + connect (Enter)"
          onClick={() => void save()}
          disabled={!canSave()}
          primary
        />
      </PromptActions>
    </ModalShell>
  );
}
