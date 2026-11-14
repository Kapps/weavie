import { ArrowUp, Bot, GitBranch, Laptop } from "lucide-solid";
import { createEffect, createSignal, For, type JSX, onCleanup, Show } from "solid-js";
import { backendPhase, connectedBackends, requestBranches, selectedSession } from "../bridge";
import {
  agentProviders,
  defaultAgentProvider,
  setDefaultAgentProvider,
} from "../chrome/agent-default";
import type { RailSession } from "../chrome/session-store";
import { keyHint } from "../commands/key-hint";
import { CommandIds } from "../commands/types";
import { notify } from "../notify/notify";
import { MobileActionLabel } from "./MobileActionLabel";
import { NewSessionComposer, type NewSessionSeed } from "./NewSessionComposer";
import { NewSessionControl } from "./NewSessionControl";
import { SessionInboxList } from "./SessionInboxList";

export type { NewSessionSeed } from "./NewSessionComposer";
export type { NewSessionSeedAttachment } from "./new-session-attachments";

/** The shared Sessions surface for starting, opening, and resuming sessions. */
export function SessionInbox(props: {
  sessions: RailSession[];
  initialBackendId: string;
  active: boolean;
  compact: boolean;
  onOpen: (session: RailSession) => Promise<boolean>;
  onCreate: (
    seed: () => Promise<NewSessionSeed | null>,
    backendId: string,
    providerId: string,
  ) => Promise<boolean>;
  onManageAcp: (backendId: string) => void;
}): JSX.Element {
  const [backendId, setBackendId] = createSignal(props.initialBackendId);
  const [providerId, setProviderId] = createSignal(defaultAgentProvider(props.initialBackendId));
  const [savingProvider, setSavingProvider] = createSignal(false);
  const [base, setBase] = createSignal<"source" | "main">("main");
  const [existingBranch, setExistingBranch] = createSignal("");
  const [branches, setBranches] = createSignal<string[]>([]);
  const [branchListError, setBranchListError] = createSignal("");
  const [loadingBranches, setLoadingBranches] = createSignal(false);
  const [submitting, setSubmitting] = createSignal<"new" | "existing" | null>(null);
  let wasActive = false;
  let previousBackendId = props.initialBackendId;
  let providerChosen = false;

  const selectBackend = (id: string): void => {
    if (id === backendId()) {
      return;
    }
    providerChosen = false;
    setBackendId(id);
    setProviderId(defaultAgentProvider(id));
  };

  const selectProvider = (id: string): void => {
    const selectedBackend = backendId();
    providerChosen = true;
    setProviderId(id);
    setSavingProvider(true);
    void setDefaultAgentProvider(selectedBackend, id)
      .catch((error: unknown) => {
        if (backendId() === selectedBackend && providerId() === id) {
          providerChosen = false;
          setProviderId(defaultAgentProvider(selectedBackend));
        }
        notify(
          "warn",
          `Couldn't save the agent selection: ${error instanceof Error ? error.message : String(error)}`,
        );
      })
      .finally(() => setSavingProvider(false));
  };

  createEffect(() => {
    const savedProvider = defaultAgentProvider(backendId());
    if (!providerChosen) {
      setProviderId(savedProvider);
    }
  });

  createEffect(() => {
    const active = props.active;
    const initialBackendId = props.initialBackendId;
    if (active && (!wasActive || initialBackendId !== previousBackendId)) {
      selectBackend(initialBackendId);
      setBase("main"); // A base pick is never carried into the next open.
    }
    wasActive = active;
    previousBackendId = initialBackendId;
  });

  createEffect(() => {
    const available = connectedBackends();
    if (!available.some((backend) => backend.id === backendId())) {
      selectBackend(props.initialBackendId);
    }
  });

  createEffect(() => {
    const id = backendId();
    const phase = backendPhase(id);
    if (!props.active) return;
    if (selectedSession()?.connection.id !== id) {
      setBase("main");
    }
    setBranches([]);
    setBranchListError("");
    if (phase !== "online") {
      setLoadingBranches(true);
      return;
    }
    let current = true;
    onCleanup(() => {
      current = false;
    });
    setLoadingBranches(true);
    void requestBranches(id).then(
      (result) => {
        if (!current) return;
        setBranches(result);
        setLoadingBranches(false);
      },
      (error: unknown) => {
        if (!current) return;
        setBranchListError(error instanceof Error ? error.message : String(error));
        setLoadingBranches(false);
      },
    );
  });

  const startNew = async (seed: () => Promise<NewSessionSeed | null>): Promise<boolean> => {
    setSubmitting("new");
    const created = await props.onCreate(seed, backendId(), providerId());
    setSubmitting(null);
    return created;
  };

  const openExisting = async (): Promise<void> => {
    const branch = existingBranch().trim();
    if (submitting() !== null || branch.length === 0) return;
    setSubmitting("existing");
    if (
      await props.onCreate(
        async () => ({
          branch,
          base: "main",
          existing: true,
          prompt: "",
          attachments: [],
          workflow: null,
          roles: {},
        }),
        backendId(),
        providerId(),
      )
    ) {
      setExistingBranch("");
    }
    setSubmitting(null);
  };

  const destinationFields = (locationLabel: string, providerLabel: string): JSX.Element => (
    <>
      <NewSessionControl icon={<Laptop />}>
        <select
          class="ui-field"
          aria-label={locationLabel}
          value={backendId()}
          onChange={(event) => selectBackend(event.currentTarget.value)}
        >
          <For each={connectedBackends()}>
            {(backend) => (
              <option value={backend.id}>{backend.isLocal ? "Local" : backend.name}</option>
            )}
          </For>
        </select>
      </NewSessionControl>
      <NewSessionControl icon={<Bot />}>
        <select
          class="ui-field"
          aria-label={providerLabel}
          disabled={savingProvider()}
          onChange={(event) => selectProvider(event.currentTarget.value)}
        >
          <For each={agentProviders(backendId())}>
            {(provider) => (
              <option
                value={provider.id}
                selected={provider.id === providerId()}
                disabled={!provider.available}
                title={provider.unavailableReason ?? provider.name}
              >
                {provider.name}
                {provider.available ? "" : " (Unavailable)"}
              </option>
            )}
          </For>
        </select>
      </NewSessionControl>
      <Show
        when={
          agentProviders(backendId()).find((provider) => provider.id === providerId())
            ?.unavailableReason
        }
      >
        {(reason) => (
          <small class="session-composer-destination-error" role="alert">
            {reason()}
          </small>
        )}
      </Show>
    </>
  );

  return (
    <main class="session-inbox">
      <header class="session-inbox-header">
        <img src="/weavie.png" width="32" height="32" alt="" />
        <div>
          <h1 id="session-inbox-title">Sessions</h1>
          <span>
            {props.compact
              ? "Pick up where your agents left off"
              : "Start an agent on a new or existing branch"}
          </span>
        </div>
        <button
          type="button"
          class="session-inbox-manage-acp ui-button ui-button-quiet"
          onClick={() => props.onManageAcp(backendId())}
          title={`Manage ACP agents${keyHint(CommandIds.manageAcpAgents)}`}
        >
          Manage ACP agents
        </button>
      </header>

      <div class="session-inbox-actions">
        <section class="session-inbox-action ui-card" aria-labelledby="session-start-title">
          <h2 id="session-start-title" class="ui-section-label ui-card-tag">
            Start a new session
          </h2>
          <NewSessionComposer
            active={props.active}
            backendId={backendId()}
            base={base()}
            providerId={providerId()}
            providerAvailable={agentProviders(backendId()).some(
              (provider) => provider.id === providerId() && provider.available,
            )}
            busy={submitting() !== null}
            starting={submitting() === "new"}
            destination={destinationFields("Session location", "Agent provider")}
            onBase={setBase}
            onStart={startNew}
          />
        </section>

        <div class="session-inbox-divider">
          <span>OR</span>
        </div>

        <section class="session-inbox-action ui-card" aria-labelledby="session-open-title">
          <h2 id="session-open-title" class="ui-section-label ui-card-tag">
            Open an existing branch
          </h2>
          <form
            class="session-open-branch"
            onSubmit={(event) => {
              event.preventDefault();
              void openExisting();
            }}
          >
            <div class="session-composer-field session-composer-branch">
              <NewSessionControl icon={<GitBranch />}>
                <input
                  class="ui-field ui-field-mono"
                  type="text"
                  list="session-existing-branches"
                  aria-label="Existing branch for the session"
                  autocapitalize="none"
                  autocomplete="off"
                  spellcheck={false}
                  placeholder="Choose a branch"
                  value={existingBranch()}
                  onInput={(event) => setExistingBranch(event.currentTarget.value)}
                />
              </NewSessionControl>
              <datalist id="session-existing-branches">
                <For each={branches()}>{(branch) => <option value={branch} />}</For>
              </datalist>
              <Show when={loadingBranches()}>
                <small>Loading branches…</small>
              </Show>
              <Show when={branchListError() !== ""}>
                <small role="alert">{branchListError()}</small>
              </Show>
            </div>
            <div class="session-composer-options">
              {destinationFields("Open on", "Open with")}
              <button
                type="submit"
                class="session-composer-submit ui-button ui-button-lg ui-button-primary"
                aria-label={submitting() === "existing" ? "Opening branch" : "Open"}
                disabled={submitting() !== null || existingBranch().trim().length === 0}
              >
                <MobileActionLabel icon={<ArrowUp />}>
                  {submitting() === "existing" ? "Opening…" : "Open"}
                </MobileActionLabel>
              </button>
            </div>
          </form>
        </section>
      </div>

      {/* Desktop lists sessions in the rail's session panel; touch chrome has no rail, so it lists them here. */}
      <Show when={props.compact}>
        <SessionInboxList sessions={props.sessions} onOpen={props.onOpen} />
      </Show>
    </main>
  );
}
