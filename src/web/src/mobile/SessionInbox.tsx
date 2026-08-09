import { createEffect, createSignal, For, type JSX, onCleanup, onMount, Show } from "solid-js";
import {
  AgentAttachmentStrip,
  type AgentAttachmentViewStatus,
} from "../agent/AgentAttachmentStrip";
import { agentImageError, encodeAgentImage, takePastedImages } from "../agent/pasted-images";
import { backendPhase, connectedBackends, requestBranches, selectedSession } from "../bridge";
import type { BranchPreviewState } from "../chrome/new-session-branch-preview";
import type { RailSession } from "../chrome/session-store";
import { setContext } from "../commands/context";
import { keyHint } from "../commands/key-hint";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { type NewSessionBranchActions, NewSessionBranchField } from "./NewSessionBranchField";
import { SessionInboxRow } from "./SessionInboxRow";

export interface NewSessionSeedAttachment {
  id: string;
  mime: string;
  dataB64: string;
}

export interface NewSessionSeed {
  branch: string;
  base: "source" | "main";
  existing: boolean;
  prompt: string;
  attachments: NewSessionSeedAttachment[];
}

interface NewSessionAttachmentDraft extends NewSessionSeedAttachment {
  previewUrl: string;
  status: AgentAttachmentViewStatus;
  error: string | null;
}

let attachmentSequence = 0;

/** The shared home surface for starting, opening, and resuming sessions. */
export function SessionInbox(props: {
  sessions: RailSession[];
  initialBackendId: string;
  initialProviderId: "claude" | "codex";
  active: boolean;
  onOpen: (session: RailSession) => Promise<boolean>;
  onCreate: (
    seed: NewSessionSeed,
    backendId: string,
    providerId: "claude" | "codex",
  ) => Promise<boolean>;
}): JSX.Element {
  const [prompt, setPrompt] = createSignal("");
  const [backendId, setBackendId] = createSignal(props.initialBackendId);
  const [providerId, setProviderId] = createSignal<"claude" | "codex">(props.initialProviderId);
  const [base, setBase] = createSignal<"source" | "main">("source");
  const [existingBranch, setExistingBranch] = createSignal("");
  const [branches, setBranches] = createSignal<string[]>([]);
  const [branchListError, setBranchListError] = createSignal("");
  const [loadingBranches, setLoadingBranches] = createSignal(false);
  const [submitting, setSubmitting] = createSignal<"new" | "existing" | null>(null);
  const [attachments, setAttachments] = createSignal<NewSessionAttachmentDraft[]>([]);
  const [branchPreview, setBranchPreview] = createSignal<BranchPreviewState>({
    branch: "",
    manual: false,
    status: "idle",
  });
  let branchActions: NewSessionBranchActions | undefined;
  let promptInput!: HTMLTextAreaElement;
  let wasActive = false;
  let previousBackendId = props.initialBackendId;
  let previousProviderId = props.initialProviderId;

  createEffect(() => {
    const active = props.active;
    const initialBackendId = props.initialBackendId;
    const initialProviderId = props.initialProviderId;
    if (
      active &&
      (!wasActive ||
        initialBackendId !== previousBackendId ||
        initialProviderId !== previousProviderId)
    ) {
      setBackendId(initialBackendId);
      setProviderId(initialProviderId);
    }
    wasActive = active;
    previousBackendId = initialBackendId;
    previousProviderId = initialProviderId;
  });

  createEffect(() => {
    const available = connectedBackends();
    if (!available.some((backend) => backend.id === backendId())) {
      setBackendId(props.initialBackendId);
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
    if (
      submitting() !== null ||
      branchPreview().branch.trim().length === 0 ||
      images.some((attachment) => attachment.status !== "ready")
    ) {
      return;
    }
    branchActions?.cancel();
    setSubmitting("new");
    if (
      await props.onCreate(
        {
          branch: branchPreview().branch.trim(),
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
      clearAttachments();
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

  const removeAttachment = (id: string): void => {
    setAttachments((current) => {
      const removed = current.find((attachment) => attachment.id === id);
      if (removed !== undefined) {
        URL.revokeObjectURL(removed.previewUrl);
      }
      return current.filter((attachment) => attachment.id !== id);
    });
  };

  const clearAttachments = (): void => {
    for (const attachment of attachments()) {
      URL.revokeObjectURL(attachment.previewUrl);
    }
    setAttachments([]);
  };

  const captureImagePaste = (event: ClipboardEvent): void => {
    for (const blob of takePastedImages(event)) {
      const id = `new-session-image-${Date.now().toString(36)}-${(++attachmentSequence).toString(36)}`;
      const previewUrl = URL.createObjectURL(blob);
      const draft: NewSessionAttachmentDraft = {
        id,
        mime: blob.type,
        dataB64: "",
        previewUrl,
        status: "reading",
        error: null,
      };
      setAttachments((current) => [...current, draft]);
      void encodeAgentImage(blob).then(
        (dataB64) => {
          const error = agentImageError(blob.type, dataB64);
          setAttachments((current) =>
            current.map((attachment) =>
              attachment.id === id
                ? {
                    ...attachment,
                    dataB64,
                    status: error === null ? "ready" : "failed",
                    error,
                  }
                : attachment,
            ),
          );
        },
        (error: unknown) => {
          setAttachments((current) =>
            current.map((attachment) =>
              attachment.id === id
                ? {
                    ...attachment,
                    status: "failed",
                    error: error instanceof Error ? error.message : String(error),
                  }
                : attachment,
            ),
          );
        },
      );
    }
  };

  const canStart = (): boolean => {
    const images = attachments();
    return (
      submitting() === null &&
      branchPreview().branch.trim().length > 0 &&
      images.every((attachment) => attachment.status === "ready")
    );
  };

  createEffect(() => {
    if (!props.active) {
      setContext("newSessionPromptFocused", false);
    }
  });

  onMount(() => {
    onCleanup(
      registerCommand(CommandIds.submitNewSession, () => {
        if (!props.active || document.activeElement !== promptInput || !canStart()) {
          return false;
        }
        void submitNew();
        return true;
      }),
    );
  });

  onCleanup(() => {
    clearAttachments();
    setContext("newSessionPromptFocused", false);
  });

  const destinationFields = (locationLabel: string, providerLabel: string): JSX.Element => (
    <>
      <select
        aria-label={locationLabel}
        value={backendId()}
        onChange={(event) => setBackendId(event.currentTarget.value)}
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
        onChange={(event) => setProviderId(event.currentTarget.value as "claude" | "codex")}
      >
        <option value="claude">Claude Code</option>
        <option value="codex">Codex</option>
      </select>
    </>
  );

  return (
    <main class="session-inbox">
      <header class="session-inbox-header">
        <img src="/weavie.png" width="32" height="32" alt="" />
        <div>
          <h1>Sessions</h1>
          <span>Pick up where your agents left off</span>
        </div>
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
              onPaste={captureImagePaste}
            />
            <Show when={attachments().length > 0}>
              <AgentAttachmentStrip attachments={attachments()} onRemove={removeAttachment} />
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
              hasInput={prompt().trim().length > 0}
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

      <section class="session-inbox-list" aria-label="Available sessions">
        <Show
          when={props.sessions.length > 0}
          fallback={<p class="session-inbox-empty">No sessions yet.</p>}
        >
          <For each={props.sessions}>
            {(session) => <SessionInboxRow session={session} onOpen={props.onOpen} />}
          </For>
        </Show>
      </section>
    </main>
  );
}
