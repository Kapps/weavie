import { ArrowUp, Bot, FolderGit2, GitBranch, Laptop } from "lucide-solid";
import {
  createEffect,
  createMemo,
  createSignal,
  For,
  type JSX,
  on,
  onCleanup,
  Show,
} from "solid-js";
import { backendPhase, type ClientSession, connectedBackends, requestBranches } from "../bridge";
import {
  agentProviders,
  defaultAgentProvider,
  setDefaultAgentProvider,
} from "../chrome/agent-default";
import { chosenRepo, OTHER_REPO, sentRepo } from "../chrome/new-session-repo";
import type { RailSession } from "../chrome/session-store";
import { keyHint } from "../commands/key-hint";
import { runCommandWithFeedback } from "../commands/registry";
import { CommandIds } from "../commands/types";
import type { KnownRepo } from "../messaging/protocol-types";
import { notify } from "../notify/notify";
import { knownRepos } from "../repo-state";
import { MobileActionLabel } from "./MobileActionLabel";
import { NewSessionComposer, type NewSessionSeed } from "./NewSessionComposer";
import { NewSessionControl } from "./NewSessionControl";
import { SessionInboxList } from "./SessionInboxList";
import type { SessionsOpening } from "./sessions-opening";

export type { NewSessionSeed } from "./NewSessionComposer";
export type { NewSessionSeedAttachment } from "./new-session-attachments";

// No backend id: those are "local" or "remote:<name>".
const ADD_REMOTE = "add-remote";

/** The shared Sessions surface for starting, opening, and resuming sessions. */
export function SessionInbox(props: {
  sessions: RailSession[];
  // "From current session" branches from the opening's owner and its repo is preselected, whichever session is
  // selected by the time a request goes out. Each new opening starts over.
  opening: SessionsOpening;
  active: boolean;
  compact: boolean;
  onOpen: (session: RailSession) => Promise<boolean>;
  onCreate: (
    seed: () => Promise<NewSessionSeed | null>,
    backendId: string,
    providerId: string,
    repo: string | null,
    source: ClientSession | null,
  ) => Promise<boolean>;
  onManageAcp: (backendId: string) => void;
}): JSX.Element {
  const [backendId, setBackendId] = createSignal(props.opening.backendId);
  const [providerId, setProviderId] = createSignal(defaultAgentProvider(props.opening.backendId));
  const [savingProvider, setSavingProvider] = createSignal(false);
  // The repo picked from the list; null is the default.
  const [repoPick, setRepoPick] = createSignal<string | null>(null);
  // The path typed for "Other…": a repo no session here is in yet.
  const [repoPath, setRepoPath] = createSignal("");
  const [base, setBase] = createSignal<"source" | "main">("main");
  const [existingBranch, setExistingBranch] = createSignal("");
  const [branches, setBranches] = createSignal<string[]>([]);
  const [branchListError, setBranchListError] = createSignal("");
  const [loadingBranches, setLoadingBranches] = createSignal(false);
  const [submitting, setSubmitting] = createSignal<"new" | "existing" | null>(null);
  let providerChosen = false;
  let repoTyped: ReturnType<typeof setTimeout> | undefined;

  const sourceRepo = (): string | undefined =>
    props.opening.owner?.connection.id === backendId() ? props.opening.owner?.repo : undefined;
  const repos = (): KnownRepo[] => knownRepos(backendId());
  // What the list shows is what is sent: a repo that left the list is never named behind another's label.
  const chosen = createMemo(() =>
    chosenRepo(
      [repoPick(), sourceRepo()],
      repos().map((option) => option.repo),
    ),
  );
  const typingRepo = (): boolean => chosen() === OTHER_REPO;
  const repo = createMemo(() => sentRepo(typingRepo() ? repoPath() : chosen()));
  // A start names its repo: "Other…" with no path names none.
  const repoNamed = (): boolean => repo() !== null;
  // "From current session" branches inside that session's own repo, while that session is still on the rail.
  const source = (): ClientSession | null =>
    repo() === sourceRepo() &&
    props.sessions.some((session) => session.owner === props.opening.owner)
      ? props.opening.owner
      : null;
  const resetRepo = (): void => {
    clearTimeout(repoTyped);
    setRepoPick(null);
    setRepoPath("");
  };
  // "Add remote agent…" is an action, not a location: the list keeps the location it had.
  const pickBackend = (select: HTMLSelectElement): void => {
    if (select.value === ADD_REMOTE) {
      select.value = backendId();
      void runCommandWithFeedback(CommandIds.connectRemoteAgent);
    } else {
      selectBackend(select.value);
    }
  };
  // Picking "Other…" moves to the path it asks for, in the form it was picked in.
  const pickRepo = (select: HTMLSelectElement): void => {
    setRepoPick(select.value);
    if (select.value === OTHER_REPO) {
      select
        .closest(".session-composer-destination")
        ?.querySelector<HTMLInputElement>(".session-repo-path")
        ?.focus();
    }
  };
  // A typed path is asked about once typing pauses, so Start is ready by the time it is pressed.
  const typeRepo = (path: string): void => {
    clearTimeout(repoTyped);
    repoTyped = setTimeout(() => setRepoPath(path), 400);
  };
  // Leaving the field or pressing Enter names the path at once, ahead of the start Enter also asks for.
  const commitRepo = (path: string): void => {
    clearTimeout(repoTyped);
    setRepoPath(path);
  };
  onCleanup(() => clearTimeout(repoTyped));

  const selectBackend = (id: string): void => {
    if (id === backendId()) {
      return;
    }
    providerChosen = false;
    setBackendId(id);
    setProviderId(defaultAgentProvider(id));
    resetRepo();
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

  createEffect(
    on(
      () => props.opening,
      (opening) => {
        selectBackend(opening.backendId);
        // A base or repo pick is never carried into the next open.
        setBase("main");
        resetRepo();
      },
      { defer: true },
    ),
  );

  createEffect(() => {
    const available = connectedBackends();
    if (!available.some((backend) => backend.id === backendId())) {
      selectBackend(props.opening.backendId);
    }
  });

  createEffect(() => {
    if (source() === null) {
      setBase("main");
    }
  });

  createEffect(() => {
    const id = backendId();
    const phase = backendPhase(id);
    const target = repo();
    if (!props.active) return;
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
    void requestBranches(id, target).then(
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
    const created = await props.onCreate(seed, backendId(), providerId(), repo(), source());
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
        repo(),
        null,
      )
    ) {
      setExistingBranch("");
    }
    setSubmitting(null);
  };

  const destinationFields = (
    locationLabel: string,
    repoLabel: string,
    providerLabel: string,
  ): JSX.Element => (
    <div class="session-composer-destination">
      <NewSessionControl icon={<Laptop />}>
        <select
          class="ui-field"
          aria-label={locationLabel}
          disabled={submitting() !== null}
          value={backendId()}
          onChange={(event) => pickBackend(event.currentTarget)}
        >
          <For each={connectedBackends()}>
            {(backend) => (
              <option value={backend.id}>{backend.isLocal ? "Local" : backend.name}</option>
            )}
          </For>
          <option value={ADD_REMOTE}>Add remote agent…</option>
        </select>
      </NewSessionControl>
      <NewSessionControl icon={<Bot />}>
        <select
          class="ui-field"
          aria-label={providerLabel}
          disabled={savingProvider() || submitting() !== null}
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
      <NewSessionControl icon={<FolderGit2 />}>
        <select
          class="session-repo ui-field"
          aria-label={repoLabel}
          disabled={submitting() !== null}
          onChange={(event) => pickRepo(event.currentTarget)}
        >
          <For each={repos()}>
            {(option) => (
              <option value={option.repo} selected={option.repo === chosen()} title={option.repo}>
                {option.label}
              </option>
            )}
          </For>
          <option value={OTHER_REPO} selected={typingRepo()}>
            Other…
          </option>
        </select>
      </NewSessionControl>
      <Show when={typingRepo()}>
        <NewSessionControl icon={<FolderGit2 />}>
          <input
            class="session-repo-path ui-field ui-field-mono"
            type="text"
            aria-label="Repository path"
            disabled={submitting() !== null}
            placeholder="Path to a repository"
            autocapitalize="none"
            autocomplete="off"
            spellcheck={false}
            value={repoPath()}
            onInput={(event) => typeRepo(event.currentTarget.value)}
            onChange={(event) => commitRepo(event.currentTarget.value)}
          />
        </NewSessionControl>
      </Show>
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
    </div>
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
          class="session-inbox-manage-acp ui-button"
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
            repo={repo()}
            base={base()}
            source={source()}
            repoNamed={repoNamed()}
            providerId={providerId()}
            providerAvailable={agentProviders(backendId()).some(
              (provider) => provider.id === providerId() && provider.available,
            )}
            busy={submitting() !== null}
            starting={submitting() === "new"}
            destination={destinationFields("Session location", "Repository", "Agent provider")}
            onRepoPath={commitRepo}
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
              {destinationFields("Open on", "Open in", "Open with")}
              <button
                type="submit"
                class="session-composer-submit ui-button ui-button-lg ui-button-primary"
                aria-label={submitting() === "existing" ? "Opening branch" : "Open"}
                disabled={
                  submitting() !== null || existingBranch().trim().length === 0 || !repoNamed()
                }
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
