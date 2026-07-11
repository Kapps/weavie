import { createSignal } from "solid-js";
import { onSessionMessage, postToBackend } from "../bridge";
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
const [states, setStates] = createSignal<Record<string, AgentComposerState>>({});
let sequence = 0;

const keyOf = (backendId: string, slot: string): string => `${backendId}\u0000${slot}`;
const nextId = (prefix: string): string =>
  `${prefix}-${Date.now().toString(36)}-${(++sequence).toString(36)}`;

export function composerState(backendId: string, slot: string | null): AgentComposerState {
  return slot === null ? EMPTY : (states()[keyOf(backendId, slot)] ?? EMPTY);
}

export function setComposerDraft(backendId: string, slot: string, draft: string): void {
  update(backendId, slot, (state) => ({ ...state, draft, error: null }));
}

export function captureAgentImagePaste(
  event: ClipboardEvent,
  backendId: string,
  slot: string,
): boolean {
  const blobs = takePastedImages(event);
  for (const blob of blobs) {
    beginBlobUpload(blob, backendId, slot);
  }
  return blobs.length > 0;
}

export function removeComposerAttachment(backendId: string, slot: string, id: string): void {
  const attachment = composerState(backendId, slot).attachments.find((item) => item.id === id);
  if (attachment === undefined) {
    return;
  }
  revoke(attachment);
  update(backendId, slot, (state) => ({
    ...state,
    attachments: state.attachments.filter((item) => item.id !== id),
    error: null,
  }));
  if (attachment.status === "transferring" || attachment.status === "ready") {
    postToBackend(backendId, { type: "agent-attachment-remove", slot, id });
  }
}

/** Stages a provider skill (from the slash menu) for the next turn; ignores a duplicate. */
export function stageSkill(backendId: string, slot: string, name: string): void {
  update(backendId, slot, (state) =>
    state.skills.includes(name)
      ? state
      : { ...state, skills: [...state.skills, name], error: null },
  );
}

/** Removes a staged skill. */
export function unstageSkill(backendId: string, slot: string, name: string): void {
  update(backendId, slot, (state) => ({
    ...state,
    skills: state.skills.filter((skill) => skill !== name),
  }));
}

export function submitAgentTurn(backendId: string, slot: string): boolean {
  const state = composerState(backendId, slot);
  if (
    state.submittingId !== null ||
    state.attachments.some((attachment) => attachment.status !== "ready") ||
    (state.draft.trim().length === 0 && state.attachments.length === 0 && state.skills.length === 0)
  ) {
    return false;
  }

  const id = nextId("submission");
  update(backendId, slot, (current) => ({ ...current, submittingId: id, error: null }));
  postToBackend(backendId, {
    type: "agent-submit",
    slot,
    id,
    prompt: state.draft.trim(),
    attachmentIds: state.attachments.map((attachment) => attachment.id),
    skills: state.skills,
  });
  return true;
}

export function uploadAgentImage(
  backendId: string,
  slot: string,
  mime: string,
  dataB64: string,
  previewUrl: string,
): void {
  const id = nextId("attachment");
  const error = agentImageError(mime, dataB64);
  addAttachment(backendId, slot, {
    id,
    mime,
    previewUrl,
    status: error === null ? "transferring" : "failed",
    error,
  });
  if (error === null) {
    postToBackend(backendId, { type: "agent-attachment-upload", slot, id, mime, dataB64 });
  }
}

function beginBlobUpload(blob: Blob, backendId: string, slot: string): void {
  const id = nextId("attachment");
  const previewUrl = URL.createObjectURL(blob);
  addAttachment(backendId, slot, {
    id,
    mime: blob.type,
    previewUrl,
    status: "reading",
    error: null,
  });
  void encodeAgentImage(blob).then(
    (dataB64) => {
      if (!hasAttachment(backendId, slot, id)) {
        return;
      }
      const error = agentImageError(blob.type, dataB64);
      patchAttachment(backendId, slot, id, {
        status: error === null ? "transferring" : "failed",
        error,
      });
      if (error === null) {
        postToBackend(backendId, {
          type: "agent-attachment-upload",
          slot,
          id,
          mime: blob.type,
          dataB64,
        });
      }
    },
    (error: unknown) =>
      patchAttachment(backendId, slot, id, {
        status: "failed",
        error: error instanceof Error ? error.message : String(error),
      }),
  );
}

onSessionMessage((message, backendId) => {
  if (message.type === "agent-attachment-state") {
    if (message.status === "removed") {
      const attachment = composerState(backendId, message.slot).attachments.find(
        (item) => item.id === message.id,
      );
      if (attachment !== undefined) {
        revoke(attachment);
        update(backendId, message.slot, (state) => ({
          ...state,
          attachments: state.attachments.filter((item) => item.id !== message.id),
        }));
      }
      return;
    }
    patchAttachment(backendId, message.slot, message.id, {
      status: message.status,
      error: message.error.length === 0 ? null : message.error,
    });
    return;
  }
  if (message.type !== "agent-submission-state") {
    return;
  }
  const state = composerState(backendId, message.slot);
  if (state.submittingId !== message.id) {
    return;
  }
  if (message.status === "rejected") {
    update(backendId, message.slot, (current) => ({
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
  update(backendId, message.slot, (current) => ({
    draft: "",
    attachments: current.attachments.filter(
      (attachment) => !message.attachmentIds.includes(attachment.id),
    ),
    skills: [],
    submittingId: null,
    error: null,
  }));
});

function addAttachment(backendId: string, slot: string, attachment: AgentComposerAttachment): void {
  update(backendId, slot, (state) => ({
    ...state,
    attachments: [...state.attachments, attachment],
    error: null,
  }));
}

function patchAttachment(
  backendId: string,
  slot: string,
  id: string,
  patch: Pick<AgentComposerAttachment, "status" | "error">,
): void {
  update(backendId, slot, (state) => ({
    ...state,
    attachments: state.attachments.map((attachment) =>
      attachment.id === id ? { ...attachment, ...patch } : attachment,
    ),
  }));
}

function hasAttachment(backendId: string, slot: string, id: string): boolean {
  return composerState(backendId, slot).attachments.some((attachment) => attachment.id === id);
}

function update(
  backendId: string,
  slot: string,
  apply: (state: AgentComposerState) => AgentComposerState,
): void {
  const key = keyOf(backendId, slot);
  setStates((current) => ({ ...current, [key]: apply(current[key] ?? EMPTY) }));
}

function revoke(attachment: AgentComposerAttachment): void {
  if (attachment.previewUrl.startsWith("blob:")) {
    URL.revokeObjectURL(attachment.previewUrl);
  }
}
