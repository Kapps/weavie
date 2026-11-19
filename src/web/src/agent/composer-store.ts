import { type ClientSession, invokeCommandInSession } from "../bridge";
import { CommandIds } from "../commands/types";
import { captureCommandUsage } from "../commands/usage-store";
import * as state from "./composer-state";

export type {
  AgentAttachmentStatus,
  AgentComposerAttachment,
  AgentComposerState,
  AgentInvocation,
  AgentSelection,
} from "./composer-state";
export { clearReplyComposer, clearReplyComposers, commentedSelection } from "./composer-state";

import type { AgentInvocation, AgentSelection } from "./composer-state";

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
export const attachAgentSelection = (session: ClientSession, selection: AgentSelection): void =>
  state.attachSelection(main(session), selection);
export const sendAgentSelection = (
  session: ClientSession,
  selection: AgentSelection,
  comment: string,
): string | null => state.sendSelection(main(session), selection, comment);

type PreparedSubmission = NonNullable<ReturnType<typeof state.prepareSubmission>>;

// A reply box's composer: its own draft and attachments, sent by `deliver`.
function boxComposer(
  owner: state.ComposerOwner,
  opening: () => void,
  deliver: (submission: PreparedSubmission) => void,
) {
  return {
    state: () => state.composerState(owner),
    setDraft: (draft: string) => state.setComposerDraft(owner, draft),
    setError: (error: string) => state.setComposerError(owner, error),
    uploadImage: (blob: Blob) => state.uploadAgentImage(owner, blob),
    attachSelection: (selection: AgentSelection) => state.attachSelection(owner, selection),
    captureImagePaste: (event: ClipboardEvent) => state.captureAgentImagePaste(event, owner),
    removeAttachment: (id: string) => state.removeComposerAttachment(owner, id),
    setOpen: (open: boolean) => {
      state.setReplyOpen(owner, open);
      if (open) opening();
    },
    submit: (): boolean => {
      const submission = state.prepareSubmission(
        owner,
        state.composerState(owner).draft.trim(),
        null,
      );
      if (submission === null) return false;
      deliver(submission);
      return true;
    },
  };
}

export type ReplyComposer = ReturnType<typeof boxComposer>;

/** The composer of a BTW's reply box. */
export function replyComposer(session: ClientSession, conversationId: string): ReplyComposer {
  const owner = state.composerOwner(session, conversationId);
  return boxComposer(
    owner,
    () => {},
    (submission) => state.publishAgent(owner, "replyAside", { conversationId, submission }),
  );
}

/**
 * The composer of a follow-up box keyed `key`: each prompt goes to the main conversation, and the box opens
 * holding `selection` so the follow-up is about the same code. `sent` receives each submission's id.
 */
export function followUpComposer(
  session: ClientSession,
  key: string,
  selection: AgentSelection,
  sent: (submissionId: string) => void,
): ReplyComposer {
  const owner = state.composerOwner(session, key);
  return boxComposer(
    owner,
    () => {
      if (state.composerState(owner).attachments.length === 0)
        state.attachSelection(owner, selection);
    },
    (submission) => {
      state.publishAgent(owner, "submit", submission);
      sent(submission.id);
    },
  );
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
  query: string,
): boolean {
  const submission = state.prepareSubmission(main(session), prompt, invocation);
  if (submission === null) return false;
  const record = captureCommandUsage(session.connection.id, CommandIds.askAgentAside, query);
  void invokeCommandInSession(session, CommandIds.askAgentAside, {
    question: submission.prompt,
    submissionId: submission.id,
    kind: submission.kind,
    commandName: submission.commandName,
    attachmentIds: submission.attachmentIds,
    selections: submission.selections,
  }).then((result) => {
    if (result.ok && !result.cancelled) record();
    state.settleSubmission(main(session), {
      id: submission.id,
      attachmentIds: result.ok
        ? [...submission.attachmentIds, ...submission.selections.map((selection) => selection.id)]
        : [],
      status: result.ok ? "accepted" : "rejected",
      error: result.error ?? "",
    });
  });
  return true;
}
