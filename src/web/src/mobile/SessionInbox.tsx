import { createEffect, createSignal, For, type JSX, onCleanup, onMount, Show } from "solid-js";
import { AgentAttachmentStrip } from "../agent/AgentAttachmentStrip";
import { backendPhase, connectedBackends, requestBranches, selectedSession } from "../bridge";
import {
  agentProviders,
  defaultAgentProvider,
  setDefaultAgentProvider,
} from "../chrome/agent-default";
import { ContextMenu, type ContextMenuState } from "../chrome/ContextMenu";
import type { BranchPreviewState } from "../chrome/new-session-branch-preview";
import { sessionMenuAt } from "../chrome/session-menu";
import type { RailSession } from "../chrome/session-store";
import { readClipboardContent } from "../clipboard-read";
import { setContext } from "../commands/context";
import { keyHint } from "../commands/key-hint";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { notify } from "../notify/notify";
import { holdToOpen } from "./long-press";
import { type NewSessionBranchActions, NewSessionBranchField } from "./NewSessionBranchField";
import {
  createNewSessionAttachments,
  type NewSessionSeedAttachment,
} from "./new-session-attachments";
import { SessionInboxRow } from "./SessionInboxRow";

export type { NewSessionSeedAttachment } from "./new-session-attachments";

export interface NewSessionSeed {
  branch: string;
  base: "source" | "main";
  existing: boolean;
  prompt: string;
  attachments: NewSessionSeedAttachment[];
}

/** The shared Sessions surface for starting, opening, and resuming sessions. */
export function SessionInbox(props: {
  sessions: RailSession[];
  initialBackendId: string;
  active: boolean;
  compact: boolean;
  onOpen: (session: RailSession) => Promise<boolean>;
  onCreate: (seed: NewSessionSeed, backendId: string, providerId: string) => Promise<boolean>;
  onManageAcp: (backendId: string) => void;
}): JSX.Element {
  const [prompt, setPrompt] = createSignal("");
  const [backendId, setBackendId] = createSignal(props.initialBackendId);
  const [providerId, setProviderId] = createSignal(defaultAgentProvider(props.initialBackendId));
  const [savingProvider, setSavingProvider] = createSignal(false);
  const [base, setBase] = createSignal<"source" | "main">("source");
  const [existingBranch, setExistingBranch] = createSignal("");
  const [branches, setBranches] = createSignal<string[]>([]);
  const [branchListError, setBranchListError] = createSignal("");
  const [loadingBranches, setLoadingBranches] = createSignal(false);
  const [submitting, setSubmitting] = createSignal<"new" | "existing" | null>(null);
  const attachmentStore = createNewSessionAttachments();
  const attachments = attachmentStore.attachments;
  const [sessionMenu, setSessionMenu] = createSignal<ContextMenuState | null>(null);
  const [branchPreview, setBranchPreview] = createSignal<BranchPreviewState>({
    branch: "",
    error: null,
    manual: false,
    status: "idle",
  });
  let branchActions: NewSessionBranchActions | undefined;
  let promptInput!: HTMLTextAreaElement;
  let wasActive = false;
  let previousBackendId = props.initialBackendId;
  let providerChosen = false;

  // Touch chrome has no rail, so the list is where a session is managed. The gesture lives on the list, not on
  // a row: rows are rebuilt on every catalog tick, which would drop a hold in progress. Desktop opts out — the
  // inbox is a modal there, which can host neither this menu nor the confirm a delete raises.
  const openSessionMenu = (session: RailSession, x: number, y: number): void => {
    setSessionMenu(sessionMenuAt(session, x, y, false));
  };
  const holdRow = holdToOpen((x, y, pressed) => {
    const row = pressed instanceof Element ? pressed.closest("[data-session-id]") : null;
    if (!props.compact || row === null) {
      return false;
    }
    // Resolved by identity against the live list, so the menu describes the session as it is now.
    const session = props.sessions.find(
      (candidate) =>
        candidate.id === row.getAttribute("data-session-id") &&
        candidate.backendId === row.getAttribute("data-backend-id"),
    );
    if (session === undefined) {
      return false;
    }
    openSessionMenu(session, x, y);
    return true;
  });

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

  const submitNew = async (): Promise<void> => {
    const text = prompt().trim();
    const images = attachments();
    if (submitting() !== null || images.some((attachment) => attachment.status !== "ready")) {
      return;
    }
    setSubmitting("new");
    // Starting is the last word on the prompt, so it names the branch now if nothing has landed yet.
    const branch = (await branchActions?.resolve()) ?? "";
    if (branch.length === 0) {
      setSubmitting(null);
      return;
    }
    if (
      await props.onCreate(
        {
          branch,
          base: base(),
          existing: false,
          prompt: text,
          attachments: images.map(({ id, mime, dataB64 }) => ({ id, mime, dataB64 })),
        },
        backendId(),
        providerId(),
      )
    ) {
      setPrompt("");
      attachmentStore.clear();
      branchActions?.reset();
    }
    setSubmitting(null);
  };

  const openExisting = async (): Promise<void> => {
    const branch = existingBranch().trim();
    if (submitting() !== null || branch.length === 0) return;
    setSubmitting("existing");
    if (
      await props.onCreate(
        { branch, base: "main", existing: true, prompt: "", attachments: [] },
        backendId(),
        providerId(),
      )
    ) {
      setExistingBranch("");
    }
    setSubmitting(null);
  };

  const paste = async (selectionStart: number, selectionEnd: number): Promise<void> => {
    try {
      const content = await readClipboardContent();
      if (content.kind === "image") {
        attachmentStore.addEncodedImage(content.mime, content.dataB64);
        return;
      }
      if (content.kind !== "text") {
        return;
      }
      const current = prompt();
      const next = current.slice(0, selectionStart) + content.text + current.slice(selectionEnd);
      setPrompt(next);
      queueMicrotask(() => {
        if (document.activeElement === promptInput && promptInput.value === next) {
          const caret = selectionStart + content.text.length;
          promptInput.setSelectionRange(caret, caret);
        }
      });
    } catch (error) {
      notify(
        "warn",
        `Couldn't paste from the clipboard: ${error instanceof Error ? error.message : String(error)}`,
      );
    }
  };

  // A name in the field is enough on its own — a session needs no prompt; without one there has to be
  // something left to name the branch from.
  const named = (): boolean => {
    const preview = branchPreview();
    return (
      preview.branch.trim().length > 0 ||
      ((prompt().trim().length > 0 || attachments().length > 0) && preview.status !== "error")
    );
  };

  const canStart = (): boolean => {
    const images = attachments();
    return (
      submitting() === null &&
      named() &&
      agentProviders(backendId()).some(
        (provider) => provider.id === providerId() && provider.available,
      ) &&
      images.every((attachment) => attachment.status === "ready")
    );
  };

  createEffect(() => {
    if (!props.active) {
      setContext("newSessionPromptFocused", false);
    }
  });

  onMount(() => {
    const cleanups = [
      registerCommand(CommandIds.pasteNewSession, () => {
        if (!props.active || document.activeElement !== promptInput) {
          return false;
        }
        return paste(promptInput.selectionStart, promptInput.selectionEnd);
      }),
      registerCommand(CommandIds.submitNewSession, () => {
        if (!props.active || document.activeElement !== promptInput || !canStart()) {
          return false;
        }
        void submitNew();
        return true;
      }),
    ];
    onCleanup(() => {
      for (const cleanup of cleanups) {
        cleanup();
      }
    });
  });

  onCleanup(() => {
    attachmentStore.clear();
    setContext("newSessionPromptFocused", false);
  });

  const destinationFields = (locationLabel: string, providerLabel: string): JSX.Element => (
    <>
      <select
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
      <select
        aria-label={providerLabel}
        value={providerId()}
        disabled={savingProvider()}
        onChange={(event) => selectProvider(event.currentTarget.value)}
      >
        <For each={agentProviders(backendId())}>
          {(provider) => (
            <option
              value={provider.id}
              disabled={!provider.available}
              title={provider.unavailableReason ?? provider.name}
            >
              {provider.name}
              {provider.available ? "" : " (Unavailable)"}
            </option>
          )}
        </For>
      </select>
      <Show
        when={
          agentProviders(backendId()).find((provider) => provider.id === providerId())
            ?.unavailableReason
        }
      >
        {(reason) => <small role="alert">{reason()}</small>}
      </Show>
    </>
  );

  return (
    <main class="session-inbox">
      <header class="session-inbox-header">
        <img src="/weavie.png" width="32" height="32" alt="" />
        <div>
          <h1 id="session-inbox-title">Sessions</h1>
          <span>Pick up where your agents left off</span>
        </div>
        <button
          type="button"
          class="session-inbox-manage-acp"
          onClick={() => props.onManageAcp(backendId())}
          title={`Manage ACP agents${keyHint(CommandIds.manageAcpAgents)}`}
        >
          Manage ACP agents
        </button>
      </header>

      <div class="session-inbox-actions">
        <section class="session-inbox-action" aria-labelledby="session-start-title">
          <h2 id="session-start-title">Start a new session</h2>
          <form
            class="session-composer"
            onSubmit={(event) => {
              event.preventDefault();
              void submitNew();
            }}
            // Focus landing elsewhere in the composer means the draft is done; leaving it entirely
            // (closing Sessions) must not spend a query on a draft nobody submitted, and landing on the
            // branch field means the user is naming it themselves.
            onFocusOut={(event) => {
              const next = event.relatedTarget;
              if (
                next instanceof Element &&
                event.currentTarget.contains(next) &&
                next.closest(".session-composer-branch") === null
              ) {
                branchActions?.flush();
              }
            }}
          >
            <textarea
              ref={promptInput}
              aria-label="Prompt for a new session"
              placeholder="What do you want to work on?"
              rows={3}
              value={prompt()}
              onFocus={() => setContext("newSessionPromptFocused", props.active)}
              onBlur={() => setContext("newSessionPromptFocused", false)}
              onInput={(event) => setPrompt(event.currentTarget.value)}
              onPaste={attachmentStore.capturePaste}
            />
            <Show when={attachments().length > 0}>
              <AgentAttachmentStrip attachments={attachments()} onRemove={attachmentStore.remove} />
            </Show>
            <Show when={attachments().find((attachment) => attachment.error !== null)}>
              {(attachment) => (
                <div class="session-composer-error" role="alert">
                  {attachment().error}
                </div>
              )}
            </Show>
            <div class="session-composer-source">
              <label>
                <span>From</span>
                <select
                  aria-label="Branch starting point"
                  value={base()}
                  onChange={(event) => setBase(event.currentTarget.value as "source" | "main")}
                >
                  <option
                    value="source"
                    disabled={selectedSession()?.connection.id !== backendId()}
                  >
                    Current session
                  </option>
                  <option value="main">Main branch</option>
                </select>
              </label>
            </div>
            <NewSessionBranchField
              active={props.active}
              backendId={backendId()}
              attachments={attachments().map(({ id, mime, dataB64 }) => ({ id, mime, dataB64 }))}
              inputReady={
                (prompt().trim().length > 0 || attachments().length > 0) &&
                attachments().every((attachment) => attachment.status === "ready")
              }
              prompt={prompt()}
              providerId={providerId()}
              onChange={setBranchPreview}
              register={(actions) => {
                branchActions = actions;
              }}
            />
            <div class="session-composer-options">
              {destinationFields("Session location", "Agent provider")}
              <button
                type="submit"
                class="session-composer-submit mobile-primary-action"
                aria-label={submitting() === "new" ? "Starting session" : "Start"}
                title={`Start${keyHint(CommandIds.submitNewSession)}`}
                disabled={!canStart()}
              >
                <span class="mobile-action-wide">
                  {submitting() === "new" ? "Starting…" : "Start"}
                </span>
                <span class="mobile-action-compact mobile-action-submit" aria-hidden="true" />
              </button>
            </div>
          </form>
        </section>

        <div class="session-inbox-divider">
          <span>OR</span>
        </div>

        <section class="session-inbox-action" aria-labelledby="session-open-title">
          <h2 id="session-open-title">Open an existing branch</h2>
          <form
            class="session-open-branch"
            onSubmit={(event) => {
              event.preventDefault();
              void openExisting();
            }}
          >
            <label class="session-composer-branch">
              <span>Branch</span>
              <input
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
              <datalist id="session-existing-branches">
                <For each={branches()}>{(branch) => <option value={branch} />}</For>
              </datalist>
              <Show when={loadingBranches()}>
                <small>Loading branches…</small>
              </Show>
              <Show when={branchListError() !== ""}>
                <small role="alert">{branchListError()}</small>
              </Show>
            </label>
            <div class="session-composer-options">
              {destinationFields("Open on", "Open with")}
              <button
                type="submit"
                class="session-composer-submit mobile-primary-action"
                aria-label={submitting() === "existing" ? "Opening branch" : "Open"}
                disabled={submitting() !== null || existingBranch().trim().length === 0}
              >
                <span class="mobile-action-wide">
                  {submitting() === "existing" ? "Opening…" : "Open"}
                </span>
                <span class="mobile-action-compact mobile-action-submit" aria-hidden="true" />
              </button>
            </div>
          </form>
        </section>
      </div>

      <section class="session-inbox-list" aria-label="Available sessions" {...holdRow}>
        <Show
          when={props.sessions.length > 0}
          fallback={<p class="session-inbox-empty">No sessions yet.</p>}
        >
          <For each={props.sessions}>
            {(session) => (
              <SessionInboxRow
                session={session}
                compact={props.compact}
                onOpen={props.onOpen}
                onManage={openSessionMenu}
              />
            )}
          </For>
        </Show>
      </section>

      <Show when={sessionMenu()}>
        {(menu) => <ContextMenu menu={menu()} onClose={() => setSessionMenu(null)} />}
      </Show>
    </main>
  );
}
