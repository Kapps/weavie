import { type JSX, onCleanup, onMount, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { keyHint } from "../commands/key-hint";
import { CommandIds } from "../commands/types";
import { AgentAttachmentStrip } from "./AgentAttachmentStrip";
import { registerComposerPasteTarget } from "./composer-clipboard";
import { replyComposer } from "./composer-store";
import { agentImageBlob } from "./pasted-images";

export function AgentAsideReply(props: {
  session: ClientSession;
  conversationId: string;
  onClose: () => void;
}): JSX.Element {
  const composer = replyComposer(props.session, props.conversationId);
  let textarea!: HTMLTextAreaElement;
  const canSubmit = () => {
    const state = composer.state();
    return (
      state.pendingSubmission === null &&
      state.attachments.every((attachment) => attachment.status === "ready") &&
      (state.draft.trim().length > 0 || state.attachments.length > 0)
    );
  };
  onMount(() => {
    onCleanup(
      registerComposerPasteTarget(textarea, () => ({
        session: props.session,
        draft: () => composer.state().draft,
        setDraft: composer.setDraft,
        pasteImage: (mime, dataB64) => composer.uploadImage(agentImageBlob(mime, dataB64)),
      })),
    );
    queueMicrotask(() => {
      if (textarea.isConnected) textarea.focus();
    });
  });
  const submit = (): void => {
    if (canSubmit()) composer.submit();
  };

  return (
    <div class="agent-aside-reply">
      <Show when={composer.state().attachments.length > 0}>
        <AgentAttachmentStrip
          attachments={composer.state().attachments}
          onRemove={composer.removeAttachment}
        />
      </Show>
      <textarea
        ref={textarea}
        data-agent-paste-target
        aria-label="Reply to BTW"
        title={`Paste text or images${keyHint(CommandIds.agentPaste)}`}
        rows={2}
        value={composer.state().draft}
        onInput={(event) => composer.setDraft(event.currentTarget.value)}
        onPaste={composer.captureImagePaste}
        onKeyDown={(event) => {
          if (event.key === "Escape") {
            event.preventDefault();
            props.onClose();
          } else if (event.key === "Enter" && !event.shiftKey && !event.isComposing) {
            event.preventDefault();
            submit();
          }
        }}
      />
      <Show when={composer.state().error !== null}>
        <div class="agent-compose-error">{composer.state().error}</div>
      </Show>
      <div class="agent-aside-reply-actions">
        <button type="button" onClick={props.onClose}>
          Cancel
        </button>
        <button type="button" disabled={!canSubmit()} onClick={submit}>
          {composer.state().pendingSubmission !== null ? "Sending…" : "Reply"}
        </button>
      </div>
    </div>
  );
}
