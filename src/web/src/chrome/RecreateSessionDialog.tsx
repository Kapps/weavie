import { createSignal, For, type JSX, onCleanup, Show } from "solid-js";
import { dispatchCommand, registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { agentProviders } from "./agent-default";
import { ModalShell } from "./ModalShell";
import { type RailSession, sessions } from "./session-store";

/** Owns the captured session target throughout provider selection and confirmation. */
export function RecreateSessionPrompt(): JSX.Element {
  const [target, setTarget] = createSignal<RailSession | null>(null);
  onCleanup(
    registerCommand(CommandIds.recreateSessionPrompt, (args, context) => {
      const fields = args as { id?: string; backendId?: string } | undefined;
      const id = fields?.id ?? context.session?.address.slot;
      const backendId = fields?.backendId ?? context.session?.connection.id;
      const session = sessions().find((entry) => entry.id === id && entry.backendId === backendId);
      if (session === undefined) return false;
      setTarget(session);
    }),
  );
  return (
    <Show when={target()} keyed>
      {(session) => <RecreateSessionDialog session={session} onClose={() => setTarget(null)} />}
    </Show>
  );
}

function RecreateSessionDialog(props: { session: RailSession; onClose: () => void }): JSX.Element {
  const [providerId, setProviderId] = createSignal(props.session.providerId);
  const [confirming, setConfirming] = createSignal(false);
  const [busy, setBusy] = createSignal(false);
  const [error, setError] = createSignal("");
  const providers = () => agentProviders(props.session.backendId);
  const provider = () => providers().find((entry) => entry.id === providerId());
  const close = () => {
    if (!busy()) props.onClose();
  };
  const submit = async () => {
    if (busy() || !provider()?.available) return;
    if (!confirming()) {
      setConfirming(true);
      return;
    }
    setBusy(true);
    setError("");
    try {
      const result = await dispatchCommand(CommandIds.recreateSession, {
        id: props.session.id,
        backendId: props.session.backendId,
        agentProviderId: providerId(),
      });
      if (!result.ok) throw new Error(result.error ?? "Couldn't recreate the session.");
      props.onClose();
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : String(failure));
    } finally {
      setBusy(false);
    }
  };
  return (
    <ModalShell
      labelledBy="recreate-session-title"
      onDismiss={close}
      onKeyDown={(event) => {
        if (event.key === "Escape") {
          event.preventDefault();
          event.stopPropagation();
          close();
        }
      }}
    >
      <form
        onSubmit={(event) => {
          event.preventDefault();
          void submit();
        }}
      >
        <div class="confirm-title" id="recreate-session-title">
          Recreate with…
        </div>
        <div class="confirm-body">
          <Show
            when={confirming()}
            fallback={
              <label>
                Agent for “{props.session.label}”
                <select
                  class="confirm-select"
                  aria-label="Agent"
                  value={providerId()}
                  onChange={(event) => setProviderId(event.currentTarget.value)}
                  ref={(element) => queueMicrotask(() => element.focus())}
                >
                  <For each={providers()}>
                    {(entry) => (
                      <option
                        value={entry.id}
                        disabled={!entry.available}
                        title={entry.unavailableReason ?? entry.name}
                      >
                        {entry.name}
                        {entry.available ? "" : " (Unavailable)"}
                      </option>
                    )}
                  </For>
                </select>
              </label>
            }
          >
            <p>
              Start a fresh conversation with {provider()?.name} in “{props.session.label}”?
            </p>
            <p>
              Your files, edits, and editor tabs will stay. Running agent and shell work will stop,
              and shell terminals will restart. Previous conversations won't be resumed.
            </p>
          </Show>
          <Show when={error()}>
            <p role="alert">{error()}</p>
          </Show>
        </div>
        <div class="confirm-actions">
          <button type="button" class="confirm-btn" disabled={busy()} onClick={close}>
            Cancel
          </button>
          <button type="submit" class="confirm-btn" disabled={busy() || !provider()?.available}>
            {busy() ? "Recreating…" : confirming() ? "Recreate session" : "Continue"}
          </button>
        </div>
      </form>
    </ModalShell>
  );
}
