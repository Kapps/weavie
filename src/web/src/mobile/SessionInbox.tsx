import { createEffect, createSignal, For, type JSX, onCleanup, Show } from "solid-js";
import {
  AgentAttachmentStrip,
  type AgentAttachmentViewStatus,
} from "../agent/AgentAttachmentStrip";
import { agentImageError, encodeAgentImage, takePastedImages } from "../agent/pasted-images";
import { connectedBackends } from "../bridge";
import type { BranchPreviewState } from "../chrome/new-session-branch-preview";
import type { RailSession } from "../chrome/session-store";
import { type NewSessionBranchActions, NewSessionBranchField } from "./NewSessionBranchField";
import { SessionInboxRow } from "./SessionInboxRow";

export interface NewSessionSeedAttachment {
  id: string;
  mime: string;
  dataB64: string;
}

export interface NewSessionSeed {
  branch: string;
  prompt: string;
  attachments: NewSessionSeedAttachment[];
}

interface NewSessionAttachmentDraft extends NewSessionSeedAttachment {
  previewUrl: string;
  status: AgentAttachmentViewStatus;
  error: string | null;
}

let attachmentSequence = 0;

/** The compact home surface: all sessions plus a prompt-first path to a new worktree session. */
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
  onMore: () => void;
  moreTitle: string;
}): JSX.Element {
  const [prompt, setPrompt] = createSignal("");
  const [backendId, setBackendId] = createSignal(props.initialBackendId);
  const [providerId, setProviderId] = createSignal<"claude" | "codex">(props.initialProviderId);
  const [submitting, setSubmitting] = createSignal(false);
  const [attachments, setAttachments] = createSignal<NewSessionAttachmentDraft[]>([]);
  const [branchPreview, setBranchPreview] = createSignal<BranchPreviewState>({
    branch: "",
    manual: false,
    status: "idle",
  });
  let branchActions: NewSessionBranchActions | undefined;

  createEffect(() => {
    if (!connectedBackends().some((backend) => backend.id === backendId())) {
      setBackendId("local");
    }
  });

  const submit = async (): Promise<void> => {
    const text = prompt().trim();
    const images = attachments();
    if (
      submitting() ||
      branchPreview().branch.trim().length === 0 ||
      images.some((attachment) => attachment.status !== "ready") ||
      (text.length === 0 && images.length === 0)
    ) {
      return;
    }
    const branch = branchPreview().branch.trim();
    branchActions?.cancel();
    setSubmitting(true);
    if (
      await props.onCreate(
        {
          branch,
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
    setSubmitting(false);
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

  onCleanup(clearAttachments);

  const canSubmit = (): boolean => {
    const images = attachments();
    return (
      !submitting() &&
      branchPreview().branch.trim().length > 0 &&
      images.every((attachment) => attachment.status === "ready") &&
      (prompt().trim().length > 0 || images.length > 0)
    );
  };

  return (
    <main class="session-inbox">
      <header class="session-inbox-header">
        <img src="/weavie.png" width="32" height="32" alt="" />
        <div>
          <h1>Sessions</h1>
          <span>Pick up where your agents left off</span>
        </div>
      </header>

      <form
        class="session-composer"
        onSubmit={(event) => {
          event.preventDefault();
          void submit();
        }}
      >
        <textarea
          aria-label="Prompt for a new session"
          placeholder="Start a new session…"
          rows={3}
          value={prompt()}
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
        <NewSessionBranchField
          active={props.active}
          backendId={backendId()}
          hasInput={prompt().trim().length > 0 || attachments().length > 0}
          prompt={prompt()}
          providerId={providerId()}
          onChange={setBranchPreview}
          register={(actions) => {
            branchActions = actions;
          }}
        />
        <div class="session-composer-options">
          <select
            aria-label="Session location"
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
            aria-label="Agent provider"
            value={providerId()}
            onChange={(event) => setProviderId(event.currentTarget.value as "claude" | "codex")}
          >
            <option value="claude">Claude Code</option>
            <option value="codex">Codex</option>
          </select>
          <button
            type="button"
            class="session-composer-more"
            aria-label="More…"
            title={props.moreTitle}
            onClick={() => {
              branchActions?.cancel();
              props.onMore();
            }}
          >
            <span class="mobile-action-wide">More…</span>
            <span class="mobile-action-compact mobile-action-more" aria-hidden="true" />
          </button>
          <button
            type="submit"
            class="session-composer-submit mobile-primary-action"
            aria-label={submitting() ? "Starting session" : "Start"}
            disabled={!canSubmit()}
          >
            <span class="mobile-action-wide">{submitting() ? "Starting…" : "Start"}</span>
            <span class="mobile-action-compact mobile-action-submit" aria-hidden="true" />
          </button>
        </div>
      </form>

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
