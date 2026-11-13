import { type ClientSession, registerSessionFeature } from "../bridge";
import { recordCommandUsage, slashUsageId } from "../commands/usage-store";
import { persistSessionDraft, sessionDraft } from "../messaging/session-drafts";
import { createSessionOwnedResource } from "../messaging/session-owned-state";
import { encodeAgentImage, takePastedImages } from "./pasted-images";

export type AgentAttachmentStatus = "reading" | "transferring" | "ready" | "failed";

export interface AgentImageAttachment {
  kind: "image";
  id: string;
  mime: string;
  previewUrl: string;
  status: AgentAttachmentStatus;
  error: string | null;
}

/** Values the user selected in the pane; they ride the submission as text and need no upload. */
export interface AgentSelectionAttachment {
  kind: "selection";
  id: string;
  label: string;
  text: string;
  status: "ready";
  error: null;
}

export type AgentComposerAttachment = AgentImageAttachment | AgentSelectionAttachment;

/** What a selection attachment carries: its chip label and the values the agent reads. */
export interface AgentSelection {
  label: string;
  text: string;
}

export interface AgentComposerState {
  draft: string;
  attachments: AgentComposerAttachment[];
  draftRevision: number;
  pendingSubmission: {
    id: string;
    draftRevision: number;
    usage?: { id: string; query: string };
  } | null;
  error: string | null;
  // Whether a BTW reply box is open; an accepted reply closes it unless more was typed while it sent.
  replyOpen: boolean;
}

const EMPTY: AgentComposerState = {
  draft: "",
  attachments: [],
  draftRevision: 0,
  pendingSubmission: null,
  error: null,
  replyOpen: false,
};
let sequence = 0;
const DRAFT_KIND = "agent-composer";
export interface ComposerOwner {
  session: ClientSession;
  key: string | symbol;
}
interface OwnedComposer {
  owner: ComposerOwner;
  state: AgentComposerState;
}
export const MAIN_COMPOSER = Symbol("main");
const states = createSessionOwnedResource(
  (session): Map<string | symbol, OwnedComposer> =>
    new Map([
      [
        MAIN_COMPOSER,
        {
          owner: { session, key: MAIN_COMPOSER },
          state: { ...EMPTY, draft: sessionDraft(session, DRAFT_KIND) },
        },
      ],
    ]),
  (_session, owners) => {
    for (const { state } of owners.values())
      for (const attachment of state.attachments) revoke(attachment);
  },
);

export function composerOwner(session: ClientSession, key: string | symbol): ComposerOwner {
  const existing = states.get(session)?.get(key);
  if (existing !== undefined) return existing.owner;
  const owner = { session, key };
  states.update(session, (current) => new Map(current).set(key, { owner, state: EMPTY }));
  return owner;
}

export function clearReplyComposers(session: ClientSession): void {
  for (const key of states.get(session)?.keys() ?? []) {
    if (typeof key === "string") clearReplyComposer(session, key);
  }
}

export function clearReplyComposer(session: ClientSession, key: string): void {
  const owner = states.get(session)?.get(key)?.owner;
  if (owner === undefined) return;
  for (const attachment of stateFor(owner).attachments)
    removeComposerAttachment(owner, attachment.id);
  states.update(session, (current) => {
    const next = new Map(current);
    next.delete(key);
    return next;
  });
}

const nextId = (prefix: string): string =>
  `${prefix}-${Date.now().toString(36)}-${(++sequence).toString(36)}`;

export function composerState(owner: ComposerOwner | null): AgentComposerState {
  return owner === null ? EMPTY : stateFor(owner);
}

export function setReplyOpen(owner: ComposerOwner, open: boolean): void {
  update(owner, (state) => ({ ...state, replyOpen: open }));
}

export function setComposerDraft(owner: ComposerOwner, draft: string): void {
  update(owner, (state) => ({
    ...state,
    draft,
    draftRevision: state.draftRevision + 1,
    error: null,
  }));
}

export function setComposerError(owner: ComposerOwner, error: string): void {
  update(owner, (state) => ({ ...state, error }));
}

export function captureAgentImagePaste(event: ClipboardEvent, owner: ComposerOwner): boolean {
  if (!isLive(owner)) return false;
  const blobs = takePastedImages(event);
  for (const blob of blobs) {
    uploadAgentImage(owner, blob);
  }
  return blobs.length > 0;
}

export function removeComposerAttachment(owner: ComposerOwner, id: string): void {
  const attachment = stateFor(owner).attachments.find((item) => item.id === id);
  if (attachment === undefined) {
    return;
  }
  revoke(attachment);
  update(owner, (state) => ({
    ...state,
    attachments: state.attachments.filter((item) => item.id !== id),
    error: null,
  }));
  if (
    attachment.kind === "image" &&
    (attachment.status === "transferring" || attachment.status === "ready")
  ) {
    publishAgent(owner, "removeAttachment", { id });
  }
}

export function attachSelection(owner: ComposerOwner, selection: AgentSelection): void {
  if (!isLive(owner)) return;
  addAttachment(owner, {
    kind: "selection",
    id: nextId("selection"),
    ...selection,
    status: "ready",
    error: null,
  });
}

export interface AgentInvocation {
  kind: "providerCommand" | "mcpPrompt";
  name: string;
  query?: string;
}

export function prepareSubmission(
  owner: ComposerOwner,
  prompt: string,
  invocation: AgentInvocation | null,
) {
  if (!isLive(owner)) return null;
  const state = stateFor(owner);
  const command = invocation?.kind === "providerCommand";
  const refusal =
    state.pendingSubmission !== null
      ? "The previous prompt is still sending."
      : !command && state.attachments.some((attachment) => attachment.status !== "ready")
        ? "Wait for attachments to finish uploading."
        : prompt.length === 0 && !command && state.attachments.length === 0
          ? "Write a prompt or attach something before running the agent."
          : null;
  if (refusal !== null) {
    setComposerError(owner, refusal);
    return null;
  }
  const id = nextId("submission");
  update(owner, (current) => ({
    ...current,
    pendingSubmission: {
      id,
      draftRevision: current.draftRevision,
      ...(invocation === null
        ? {}
        : {
            usage: {
              id: slashUsageId(owner.session, { ...invocation, commandId: null }),
              query: invocation.query ?? invocation.name,
            },
          }),
    },
    error: null,
  }));
  return {
    id,
    prompt,
    kind: invocation?.kind ?? "prompt",
    commandName: invocation?.name ?? "",
    attachmentIds: command ? [] : images(state.attachments).map((attachment) => attachment.id),
    selections: command
      ? []
      : state.attachments
          .filter((attachment) => attachment.kind === "selection")
          .map(({ id, label, text }) => ({ id, label, text })),
  };
}

export function uploadAgentImage(owner: ComposerOwner, blob: Blob): void {
  if (!isLive(owner)) return;
  const id = nextId("attachment");
  const previewUrl = URL.createObjectURL(blob);
  addAttachment(owner, {
    kind: "image",
    id,
    mime: blob.type,
    previewUrl,
    status: "reading",
    error: null,
  });
  void encodeAgentImage(blob).then(
    ({ mime, dataB64 }) => {
      if (!hasAttachment(owner, id)) {
        return;
      }
      patchAttachment(owner, id, {
        mime,
        previewUrl: `data:${mime};base64,${dataB64}`,
        status: "transferring",
        error: null,
      });
      URL.revokeObjectURL(previewUrl);
      publishAgent(owner, "uploadAttachment", { id, mime, dataB64 });
    },
    (error: unknown) => {
      if (!hasAttachment(owner, id)) {
        return;
      }
      patchAttachment(owner, id, {
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
    const owner = [...(states.get(session)?.values() ?? [])].find(({ state }) =>
      state.attachments.some((item) => item.id === message.id),
    )?.owner;
    if (owner === undefined) return;
    if (message.status === "removed") {
      const attachment = stateFor(owner).attachments.find((item) => item.id === message.id);
      if (attachment !== undefined) {
        revoke(attachment);
        update(owner, (state) => ({
          ...state,
          attachments: state.attachments.filter((item) => item.id !== message.id),
        }));
      }
      return;
    }
    patchAttachment(owner, message.id, {
      status: message.status,
      error: message.error.length === 0 ? null : message.error,
    });
  });
  const offSubmission = feature.on<SubmissionState>("submissionState", (message) => {
    const owner = [...(states.get(session)?.values() ?? [])].find(
      ({ state }) => state.pendingSubmission?.id === message.id,
    )?.owner;
    if (owner !== undefined) settleSubmission(owner, message);
  });
  return () => {
    offAttachment();
    offSubmission();
  };
});

export function settleSubmission(owner: ComposerOwner, message: SubmissionState): void {
  const state = stateFor(owner);
  if (state.pendingSubmission?.id !== message.id) {
    return;
  }
  if (message.status === "rejected") {
    update(owner, (current) => ({
      ...current,
      pendingSubmission: null,
      error: message.error,
    }));
    return;
  }
  const usage = state.pendingSubmission.usage;
  if (usage !== undefined) recordCommandUsage(owner.session.connection.id, usage.id, usage.query);
  for (const attachment of state.attachments) {
    if (message.attachmentIds.includes(attachment.id)) {
      revoke(attachment);
    }
  }
  update(owner, (current) => {
    const draft =
      current.draftRevision === state.pendingSubmission?.draftRevision ? "" : current.draft;
    const attachments = current.attachments.filter(
      (attachment) => !message.attachmentIds.includes(attachment.id),
    );
    return {
      ...current,
      draft,
      attachments,
      pendingSubmission: null,
      error: null,
      replyOpen: current.replyOpen && (draft.length > 0 || attachments.length > 0),
    };
  });
}

export function publishAgent(
  owner: ComposerOwner,
  name: string,
  payload: Record<string, unknown>,
): void {
  if (isLive(owner)) {
    owner.session.feature("agent").publish(name, payload);
  }
}

function addAttachment(owner: ComposerOwner, attachment: AgentComposerAttachment): void {
  update(owner, (state) => ({
    ...state,
    attachments: [...state.attachments, attachment],
    error: null,
  }));
}

function patchAttachment(
  owner: ComposerOwner,
  id: string,
  patch: Partial<Pick<AgentImageAttachment, "mime" | "previewUrl" | "status" | "error">>,
): void {
  update(owner, (state) => ({
    ...state,
    attachments: state.attachments.map((attachment) =>
      attachment.id === id && attachment.kind === "image"
        ? { ...attachment, ...patch }
        : attachment,
    ),
  }));
}

function images(attachments: readonly AgentComposerAttachment[]): AgentImageAttachment[] {
  return attachments.filter((attachment) => attachment.kind === "image");
}

function hasAttachment(owner: ComposerOwner, id: string): boolean {
  return stateFor(owner).attachments.some((attachment) => attachment.id === id);
}

function update(
  owner: ComposerOwner,
  apply: (state: AgentComposerState) => AgentComposerState,
): void {
  if (isLive(owner)) {
    states.update(owner.session, (current) => {
      const next = apply(current.get(owner.key)!.state);
      if (owner.key === MAIN_COMPOSER) persistSessionDraft(owner.session, DRAFT_KIND, next.draft);
      return new Map(current).set(owner.key, { owner, state: next });
    });
  }
}

function stateFor(owner: ComposerOwner): AgentComposerState {
  return isLive(owner) ? states.get(owner.session)!.get(owner.key)!.state : EMPTY;
}

function isLive(owner: ComposerOwner): boolean {
  return !owner.session.closed && states.get(owner.session)?.get(owner.key)?.owner === owner;
}

function revoke(attachment: AgentComposerAttachment): void {
  if (attachment.kind === "image" && attachment.previewUrl.startsWith("blob:")) {
    URL.revokeObjectURL(attachment.previewUrl);
  }
}
