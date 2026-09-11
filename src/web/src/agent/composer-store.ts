import { type ClientSession, invokeCommandInSession, registerSessionFeature } from "../bridge";
import { CommandIds } from "../commands/types";
import { persistSessionDraft, sessionDraft } from "../messaging/session-drafts";
import { createSessionOwnedResource } from "../messaging/session-owned-state";
import { encodeAgentImage, takePastedImages } from "./pasted-images";

export type AgentAttachmentStatus = "reading" | "transferring" | "ready" | "failed";

export interface AgentComposerAttachment {
  id: string;
  mime: string;
  previewUrl: string;
  status: AgentAttachmentStatus;
  error: string | null;
}

export interface AgentComposerState {
  draft: string;
  attachments: AgentComposerAttachment[];
  draftRevision: number;
  pendingSubmission: { id: string; draftRevision: number } | null;
  error: string | null;
}

const EMPTY: AgentComposerState = {
  draft: "",
  attachments: [],
  draftRevision: 0,
  pendingSubmission: null,
  error: null,
};
let sequence = 0;
const DRAFT_KIND = "agent-composer";
const states = createSessionOwnedResource(
  (session): AgentComposerState => ({ ...EMPTY, draft: sessionDraft(session, DRAFT_KIND) }),
  (_session, state) => {
    for (const attachment of state.attachments) revoke(attachment);
  },
);

const nextId = (prefix: string): string =>
  `${prefix}-${Date.now().toString(36)}-${(++sequence).toString(36)}`;

export function composerState(session: ClientSession | null): AgentComposerState {
  return session === null ? EMPTY : stateFor(session);
}

export function setComposerDraft(session: ClientSession, draft: string): void {
  update(session, (state) => ({
    ...state,
    draft,
    draftRevision: state.draftRevision + 1,
    error: null,
  }));
}

export function setComposerError(session: ClientSession, error: string): void {
  update(session, (state) => ({ ...state, error }));
}

export function captureAgentImagePaste(event: ClipboardEvent, session: ClientSession): boolean {
  const blobs = takePastedImages(event);
  for (const blob of blobs) {
    uploadAgentImage(session, blob);
  }
  return blobs.length > 0;
}

export function removeComposerAttachment(session: ClientSession, id: string): void {
  const attachment = stateFor(session).attachments.find((item) => item.id === id);
  if (attachment === undefined) {
    return;
  }
  revoke(attachment);
  update(session, (state) => ({
    ...state,
    attachments: state.attachments.filter((item) => item.id !== id),
    error: null,
  }));
  if (attachment.status === "transferring" || attachment.status === "ready") {
    publishAgent(session, "removeAttachment", { id });
  }
}

export function submitAgentTurn(session: ClientSession, commandName: string | null): boolean {
  const submission = prepareSubmission(session, stateFor(session).draft.trim(), commandName);
  if (submission === null) return false;
  publishAgent(session, "submit", submission);
  return true;
}

export function submitAgentAside(session: ClientSession, prompt: string): boolean {
  const submission = prepareSubmission(session, prompt, null);
  if (submission === null) return false;
  void invokeCommandInSession(session, CommandIds.askAgentAside, {
    question: submission.prompt,
    submissionId: submission.id,
    attachmentIds: submission.attachmentIds,
  }).then((result) =>
    settleSubmission(session, {
      id: submission.id,
      attachmentIds: result.ok ? submission.attachmentIds : [],
      status: result.ok ? "accepted" : "rejected",
      error: result.error ?? "",
    }),
  );
  return true;
}

function prepareSubmission(session: ClientSession, prompt: string, commandName: string | null) {
  const state = stateFor(session);
  const command = commandName !== null;
  if (
    state.pendingSubmission !== null ||
    (!command && state.attachments.some((attachment) => attachment.status !== "ready"))
  )
    return null;
  if (prompt.length === 0 && !command && state.attachments.length === 0) {
    setComposerError(session, "Write a prompt or attach an image before running the agent.");
    return null;
  }
  const id = nextId("submission");
  update(session, (current) => ({
    ...current,
    pendingSubmission: { id, draftRevision: current.draftRevision },
    error: null,
  }));
  return {
    id,
    prompt,
    kind: command ? "providerCommand" : "prompt",
    commandName: commandName ?? "",
    attachmentIds: command ? [] : state.attachments.map((attachment) => attachment.id),
  };
}

export function uploadAgentImage(session: ClientSession, blob: Blob): void {
  const id = nextId("attachment");
  const previewUrl = URL.createObjectURL(blob);
  addAttachment(session, {
    id,
    mime: blob.type,
    previewUrl,
    status: "reading",
    error: null,
  });
  void encodeAgentImage(blob).then(
    ({ mime, dataB64 }) => {
      if (!hasAttachment(session, id)) {
        return;
      }
      patchAttachment(session, id, {
        mime,
        previewUrl: `data:${mime};base64,${dataB64}`,
        status: "transferring",
        error: null,
      });
      URL.revokeObjectURL(previewUrl);
      publishAgent(session, "uploadAttachment", { id, mime, dataB64 });
    },
    (error: unknown) => {
      if (!hasAttachment(session, id)) {
        return;
      }
      patchAttachment(session, id, {
        status: "failed",
        error: error instanceof Error ? error.message : String(error),
      });
    },
  );
}

interface AttachmentState {
  id: string;
  status: AgentAttachmentStatus | "removed";
  error: string;
}

interface SubmissionState {
  id: string;
  attachmentIds: string[];
  status: "accepted" | "rejected";
  error: string;
}

registerSessionFeature((session) => {
  const feature = session.feature("agent");
  const offAttachment = feature.on<AttachmentState>("attachmentState", (message) => {
    if (message.status === "removed") {
      const attachment = stateFor(session).attachments.find((item) => item.id === message.id);
      if (attachment !== undefined) {
        revoke(attachment);
        update(session, (state) => ({
          ...state,
          attachments: state.attachments.filter((item) => item.id !== message.id),
        }));
      }
      return;
    }
    patchAttachment(session, message.id, {
      status: message.status,
      error: message.error.length === 0 ? null : message.error,
    });
  });
  const offSubmission = feature.on<SubmissionState>("submissionState", (message) => {
    settleSubmission(session, message);
  });
  return () => {
    offAttachment();
    offSubmission();
  };
});

function settleSubmission(session: ClientSession, message: SubmissionState): void {
  const state = stateFor(session);
  if (state.pendingSubmission?.id !== message.id) {
    return;
  }
  if (message.status === "rejected") {
    update(session, (current) => ({
      ...current,
      pendingSubmission: null,
      error: message.error,
    }));
    return;
  }
  for (const attachment of state.attachments) {
    if (message.attachmentIds.includes(attachment.id)) {
      revoke(attachment);
    }
  }
  update(session, (current) => ({
    ...current,
    draft: current.draftRevision === state.pendingSubmission?.draftRevision ? "" : current.draft,
    attachments: current.attachments.filter(
      (attachment) => !message.attachmentIds.includes(attachment.id),
    ),
    pendingSubmission: null,
    error: null,
  }));
}

function publishAgent(
  session: ClientSession,
  name: string,
  payload: Record<string, unknown>,
): void {
  if (!session.closed) {
    session.feature("agent").publish(name, payload);
  }
}

function addAttachment(session: ClientSession, attachment: AgentComposerAttachment): void {
  update(session, (state) => ({
    ...state,
    attachments: [...state.attachments, attachment],
    error: null,
  }));
}

function patchAttachment(
  session: ClientSession,
  id: string,
  patch: Partial<Pick<AgentComposerAttachment, "mime" | "previewUrl" | "status" | "error">>,
): void {
  update(session, (state) => ({
    ...state,
    attachments: state.attachments.map((attachment) =>
      attachment.id === id ? { ...attachment, ...patch } : attachment,
    ),
  }));
}

function hasAttachment(session: ClientSession, id: string): boolean {
  return stateFor(session).attachments.some((attachment) => attachment.id === id);
}

function update(
  session: ClientSession,
  apply: (state: AgentComposerState) => AgentComposerState,
): void {
  if (!session.closed) {
    states.update(session, (current) => {
      const next = apply(current);
      persistSessionDraft(session, DRAFT_KIND, next.draft);
      return next;
    });
  }
}

function stateFor(session: ClientSession): AgentComposerState {
  return states.get(session) ?? EMPTY;
}

function revoke(attachment: AgentComposerAttachment): void {
  if (attachment.previewUrl.startsWith("blob:")) {
    URL.revokeObjectURL(attachment.previewUrl);
  }
}
