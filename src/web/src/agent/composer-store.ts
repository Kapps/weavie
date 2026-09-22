import { type ClientSession, invokeCommandInSession } from "../bridge";
import { CommandIds } from "../commands/types";
import * as state from "./composer-state";

export type {
  AgentAttachmentStatus,
  AgentComposerAttachment,
  AgentComposerState,
  AgentInvocation,
} from "./composer-state";
export { clearReplyComposer, clearReplyComposers } from "./composer-state";

import type { AgentInvocation } from "./composer-state";

const main = (session: ClientSession): state.ComposerOwner =>
  state.composerOwner(session, state.MAIN_COMPOSER);
export const composerState = (session: ClientSession | null) =>
  state.composerState(session === null ? null : main(session));
export const setComposerDraft = (session: ClientSession, draft: string): void =>
  state.setComposerDraft(main(session), draft);
export const setComposerError = (session: ClientSession, error: string): void =>
  state.setComposerError(main(session), error);
export const captureAgentImagePaste = (event: ClipboardEvent, session: ClientSession): boolean =>
  state.captureAgentImagePaste(event, main(session));
export const removeComposerAttachment = (session: ClientSession, id: string): void =>
  state.removeComposerAttachment(main(session), id);
export const uploadAgentImage = (session: ClientSession, blob: Blob): void =>
  state.uploadAgentImage(main(session), blob);

export function replyComposer(session: ClientSession, conversationId: string) {
  const owner = state.composerOwner(session, conversationId);
  return {
    state: () => state.composerState(owner),
    setDraft: (draft: string) => state.setComposerDraft(owner, draft),
    setError: (error: string) => state.setComposerError(owner, error),
    uploadImage: (blob: Blob) => state.uploadAgentImage(owner, blob),
    captureImagePaste: (event: ClipboardEvent) => state.captureAgentImagePaste(event, owner),
    removeAttachment: (id: string) => state.removeComposerAttachment(owner, id),
    submit: (): boolean => {
      const submission = state.prepareSubmission(
        owner,
        state.composerState(owner).draft.trim(),
        null,
      );
      if (submission === null) return false;
      state.publishAgent(owner, "replyAside", { conversationId, submission });
      return true;
    },
  };
}

export function submitAgentTurn(
  session: ClientSession,
  invocation: AgentInvocation | null,
): boolean {
  const submission = state.prepareSubmission(
    main(session),
    composerState(session).draft.trim(),
    invocation,
  );
  if (submission === null) return false;
  state.publishAgent(main(session), "submit", submission);
  return true;
}

export function submitAgentAside(
  session: ClientSession,
  prompt: string,
  invocation: AgentInvocation | null,
): boolean {
  const submission = state.prepareSubmission(main(session), prompt, invocation);
  if (submission === null) return false;
  void invokeCommandInSession(session, CommandIds.askAgentAside, {
    question: submission.prompt,
    submissionId: submission.id,
    kind: submission.kind,
    commandName: submission.commandName,
    attachmentIds: submission.attachmentIds,
  }).then((result) =>
    state.settleSubmission(main(session), {
      id: submission.id,
      attachmentIds: result.ok ? submission.attachmentIds : [],
      status: result.ok ? "accepted" : "rejected",
      error: result.error ?? "",
    }),
  );
  return true;
}
