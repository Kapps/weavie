import { createSignal } from "solid-js";
import { type ClientSession, registerSessionFeature } from "../bridge";
import { persistSessionDraft, sessionDraft } from "../messaging/session-drafts";
import { agentImageError, encodeAgentImage, takePastedImages } from "./pasted-images";

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
  // Provider skill names staged from the slash menu; submitted as structured skill inputs and cleared on send.
  skills: string[];
  submittingId: string | null;
  error: string | null;
}

const EMPTY: AgentComposerState = {
  draft: "",
  attachments: [],
  skills: [],
  submittingId: null,
  error: null,
};
const [states, setStates] = createSignal(new Map<ClientSession, AgentComposerState>());
let sequence = 0;
const DRAFT_KIND = "agent-composer";

const nextId = (prefix: string): string =>
  `${prefix}-${Date.now().toString(36)}-${(++sequence).toString(36)}`;

export function composerState(session: ClientSession | null): AgentComposerState {
  return session === null ? EMPTY : stateFor(session);
}

export function setComposerDraft(session: ClientSession, draft: string): void {
  update(session, (state) => ({ ...state, draft, error: null }));
}

export function captureAgentImagePaste(event: ClipboardEvent, session: ClientSession): boolean {
  const blobs = takePastedImages(event);
  for (const blob of blobs) {
    beginBlobUpload(blob, session);
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

/** Stages a provider skill (from the slash menu) for the next turn; ignores a duplicate. */
export function stageSkill(session: ClientSession, name: string): void {
  update(session, (state) =>
    state.skills.includes(name)
      ? state
      : { ...state, skills: [...state.skills, name], error: null },
  );
}

/** Removes a staged skill. */
export function unstageSkill(session: ClientSession, name: string): void {
  update(session, (state) => ({
    ...state,
    skills: state.skills.filter((skill) => skill !== name),
  }));
}

export function submitAgentTurn(session: ClientSession): boolean {
  const state = stateFor(session);
  if (
    state.submittingId !== null ||
    state.attachments.some((attachment) => attachment.status !== "ready") ||
    (state.draft.trim().length === 0 && state.attachments.length === 0 && state.skills.length === 0)
  ) {
    return false;
  }

  const id = nextId("submission");
  update(session, (current) => ({ ...current, submittingId: id, error: null }));
  publishAgent(session, "submit", {
    id,
    prompt: state.draft.trim(),
    attachmentIds: state.attachments.map((attachment) => attachment.id),
    skills: state.skills,
  });
  return true;
}

export function uploadAgentImage(
  session: ClientSession,
  mime: string,
  dataB64: string,
  previewUrl: string,
): void {
  const id = nextId("attachment");
  const error = agentImageError(mime, dataB64);
  addAttachment(session, {
    id,
    mime,
    previewUrl,
    status: error === null ? "transferring" : "failed",
    error,
  });
  if (error === null) {
    publishAgent(session, "uploadAttachment", { id, mime, dataB64 });
  }
}

function beginBlobUpload(blob: Blob, session: ClientSession): void {
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
    (dataB64) => {
      if (!hasAttachment(session, id)) {
        return;
      }
      const error = agentImageError(blob.type, dataB64);
      patchAttachment(session, id, {
        status: error === null ? "transferring" : "failed",
        error,
      });
      if (error === null) {
        publishAgent(session, "uploadAttachment", {
          id,
          mime: blob.type,
          dataB64,
        });
      }
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
    const state = stateFor(session);
    if (state.submittingId !== message.id) {
      return;
    }
    if (message.status === "rejected") {
      update(session, (current) => ({
        ...current,
        submittingId: null,
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
      draft: "",
      attachments: current.attachments.filter(
        (attachment) => !message.attachmentIds.includes(attachment.id),
      ),
      skills: [],
      submittingId: null,
      error: null,
    }));
  });
  return () => {
    offAttachment();
    offSubmission();
    for (const attachment of stateFor(session).attachments) {
      revoke(attachment);
    }
    setStates((previous) => {
      const next = new Map(previous);
      next.delete(session);
      return next;
    });
  };
});

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
  patch: Pick<AgentComposerAttachment, "status" | "error">,
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
    const next = apply(stateFor(session));
    setStates((current) => new Map(current).set(session, next));
    persistSessionDraft(session, DRAFT_KIND, next.draft);
  }
}

function stateFor(session: ClientSession): AgentComposerState {
  return session.closed
    ? EMPTY
    : (states().get(session) ?? { ...EMPTY, draft: sessionDraft(session, DRAFT_KIND) });
}

function revoke(attachment: AgentComposerAttachment): void {
  if (attachment.previewUrl.startsWith("blob:")) {
    URL.revokeObjectURL(attachment.previewUrl);
  }
}
