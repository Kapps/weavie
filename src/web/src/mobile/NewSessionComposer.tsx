import { ArrowUp, GitFork } from "lucide-solid";
import {
  createEffect,
  createMemo,
  createSignal,
  type JSX,
  onCleanup,
  onMount,
  Show,
  untrack,
} from "solid-js";
import { AgentAttachmentStrip } from "../agent/AgentAttachmentStrip";
import type { ClientSession, LaunchOrigin, RolePick } from "../bridge";
import type { BranchPreviewState } from "../chrome/new-session-branch-preview";
import { readClipboardContent } from "../clipboard-read";
import { setContext } from "../commands/context";
import { keyHint } from "../commands/key-hint";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { notify } from "../notify/notify";
import { MobileActionLabel } from "./MobileActionLabel";
import { type NewSessionBranchActions, NewSessionBranchField } from "./NewSessionBranchField";
import { NewSessionControl } from "./NewSessionControl";
import { type NewSessionWorkflowActions, NewSessionWorkflowField } from "./NewSessionWorkflowField";
import {
  createNewSessionAttachments,
  type NewSessionSeedAttachment,
} from "./new-session-attachments";

export interface NewSessionSeed {
  branch: string;
  base: "source" | "main";
  existing: boolean;
  prompt: string;
  attachments: NewSessionSeedAttachment[];
  /** The workflow the session starts in, which takes the prompt and attachments; null for none. */
  workflow: string | null;
  /** What to save for each role the workflow still needs, from the session's provider. */
  roles: Record<string, RolePick>;
}

/** The prompt-first form that starts a session on a new branch. */
export function NewSessionComposer(props: {
  active: boolean;
  backendId: string;
  /** The repo the session is created in; null for the host's choice. */
  repo: string | null;
  base: "source" | "main";
  /** The session "From current session" branches from; null when there is none in the chosen repo. */
  source: ClientSession | null;
  /** Whether the destination names a repo: false while a path is still to be typed. */
  repoNamed: boolean;
  providerId: string;
  /** Whether the chosen agent provider can start a session. */
  providerAvailable: boolean;
  /** Whether any Sessions submission is in flight. */
  busy: boolean;
  /** Whether this composer's own submission is in flight. */
  starting: boolean;
  destination: JSX.Element;
  /** Names the typed repo path at once, ahead of its debounce. */
  onRepoPath: (path: string) => void;
  onBase: (base: "source" | "main") => void;
  onStart: (seed: () => Promise<NewSessionSeed | null>) => Promise<boolean>;
}): JSX.Element {
  const [prompt, setPrompt] = createSignal("");
  const attachmentStore = createNewSessionAttachments();
  const attachments = attachmentStore.attachments;
  const [branchPreview, setBranchPreview] = createSignal<BranchPreviewState>({
    branch: "",
    error: null,
    manual: false,
    status: "idle",
  });
  const [workflowReady, setWorkflowReady] = createSignal(false);
  let branchActions: NewSessionBranchActions | undefined;
  let workflowActions: NewSessionWorkflowActions | undefined;
  let promptInput!: HTMLTextAreaElement;
  // The branch name and the workflows resolve where creation does.
  const origin = createMemo<LaunchOrigin>(() => ({
    sourceId: props.base === "source" ? (props.source?.address.slot ?? null) : null,
    repo: props.repo,
  }));

  const submitNew = async (): Promise<void> => {
    const text = prompt().trim();
    const images = attachments();
    if (props.busy || images.some((attachment) => attachment.status !== "ready")) {
      return;
    }
    // The workflow is the one on screen at the press: a suggestion landing during the branch wait is not it.
    const start = workflowActions?.capture() ?? { workflow: null, roles: {} };
    if (
      await props.onStart(async () => {
        // Starting is the last word on the prompt, so it names the branch now if nothing has landed yet.
        const branch = (await branchActions?.resolve()) ?? "";
        return branch.length === 0
          ? null
          : {
              branch,
              base: props.base,
              existing: false,
              prompt: text,
              attachments: images.map(({ id, mime, dataB64 }) => ({ id, mime, dataB64 })),
              workflow: start.workflow,
              roles: start.roles,
            };
      })
    ) {
      setPrompt("");
      attachmentStore.clear();
      branchActions?.reset();
      workflowActions?.reset();
    }
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

  const canStart = (): boolean =>
    !props.busy &&
    named() &&
    workflowReady() &&
    props.repoNamed &&
    props.providerAvailable &&
    attachments().every((attachment) => attachment.status === "ready");

  // The repo a start was asked for by Enter in its path, while that repo's workflows were still loading: the
  // start runs once it can, unless the form is touched, the repo changes, or Sessions closes first.
  const [startAskedIn, setStartAskedIn] = createSignal<string | null>(null);
  createEffect(() => {
    const asked = startAskedIn();
    if (asked === null) {
      return;
    }
    if (!props.active || asked !== props.repo) {
      setStartAskedIn(null);
    } else if (canStart()) {
      setStartAskedIn(null);
      void untrack(submitNew);
    }
  });
  const starting = (): boolean => props.starting || startAskedIn() !== null;
  // Enter in the repo path names the repo and starts the session in it.
  const startFromRepoPath = (event: KeyboardEvent): void => {
    const field = event.target;
    if (
      event.key !== "Enter" ||
      event.isComposing ||
      !(field instanceof HTMLInputElement) ||
      !field.matches(".session-repo-path")
    ) {
      return;
    }
    event.preventDefault();
    props.onRepoPath(field.value);
    if (canStart()) {
      void submitNew();
    } else if (!props.busy) {
      setStartAskedIn(props.repo);
    }
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

  return (
    <form
      class="session-composer"
      onSubmit={(event) => {
        event.preventDefault();
        void submitNew();
      }}
      onKeyDown={startFromRepoPath}
      onInput={() => setStartAskedIn(null)}
      onChange={() => setStartAskedIn(null)}
      // Focus landing elsewhere in the composer means the draft is done; leaving it entirely
      // (closing Sessions) must not spend a query on a draft nobody submitted, and landing on a
      // suggested field means the user is setting that one themselves.
      onFocusOut={(event) => {
        setStartAskedIn(null);
        const next = event.relatedTarget;
        if (!(next instanceof Element) || !event.currentTarget.contains(next)) {
          return;
        }
        if (next.closest(".session-composer-workflow") === null) {
          workflowActions?.flush();
        }
        if (next.closest(".session-composer-branch") === null) {
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
      <div class="session-composer-field session-composer-source">
        <NewSessionControl icon={<GitFork />}>
          <select
            class="ui-field"
            aria-label="Branch starting point"
            disabled={props.busy}
            value={props.base}
            onChange={(event) => props.onBase(event.currentTarget.value as "source" | "main")}
          >
            <option value="main">From main branch</option>
            <option value="source" disabled={props.source === null}>
              From current session
            </option>
          </select>
        </NewSessionControl>
      </div>
      <NewSessionBranchField
        active={props.active}
        backendId={props.backendId}
        origin={origin()}
        attachments={attachments().map(({ id, mime, dataB64 }) => ({ id, mime, dataB64 }))}
        inputReady={
          (prompt().trim().length > 0 || attachments().length > 0) &&
          attachments().every((attachment) => attachment.status === "ready")
        }
        prompt={prompt()}
        onChange={setBranchPreview}
        register={(actions) => {
          branchActions = actions;
        }}
      />
      <NewSessionWorkflowField
        active={props.active}
        backendId={props.backendId}
        origin={origin()}
        providerId={props.providerId}
        prompt={prompt()}
        onReady={setWorkflowReady}
        register={(actions) => {
          workflowActions = actions;
        }}
      />
      <div class="session-composer-options">
        {props.destination}
        <button
          type="submit"
          class="session-composer-submit ui-button ui-button-lg ui-button-primary"
          aria-label={starting() ? "Starting session" : "Start"}
          title={`Start${keyHint(CommandIds.submitNewSession)}`}
          disabled={!canStart()}
        >
          <MobileActionLabel icon={<ArrowUp />}>
            {starting() ? "Starting…" : "Start"}
          </MobileActionLabel>
        </button>
      </div>
    </form>
  );
}
