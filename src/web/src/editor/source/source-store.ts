import { type ClientSession, registerSessionFeature, selectedSession } from "../../bridge";
import {
  createSessionOwnedMap,
  createSessionOwnedState,
} from "../../messaging/session-owned-state";

export interface SourceDocEntry {
  title: string;
  sourceId: string;
  markdown?: string | undefined;
  html?: string | undefined;
  editedTime: string;
  truncated: boolean;
  unknownBlocks: number;
  status: "loading" | "ready" | "error";
  message?: string;
}

export interface SourceTokenPrompt {
  session: ClientSession;
  sourceId: string;
  label: string;
}

export interface SourceEditError {
  session: ClientSession;
  target: string;
  message: string;
  stale: boolean;
}

interface SourceEditState {
  markdown: string;
  line: number;
  draft: string;
  original: string;
  saving: boolean;
  error: { message: string; stale: boolean } | undefined;
}

const documents = createSessionOwnedState<Record<string, SourceDocEntry>>(() => ({}));
const tokenPrompts = createSessionOwnedState<SourceTokenPrompt | null>(() => null);
const edits = createSessionOwnedMap<string, SourceEditState>();
const editErrorListeners = new Set<(error: SourceEditError) => void>();

export const sourceEditState = edits.get;
export const keepSourceEdit = edits.set;
export const discardSourceEdit = edits.delete;

function failSourceEdit(
  session: ClientSession,
  target: string,
  error: SourceEditState["error"],
): void {
  const edit = edits.get(session, target);
  if (edit !== undefined) {
    edit.saving = false;
    edit.error = error;
  }
}

function updateDocument(
  session: ClientSession,
  target: string,
  update: (previous: SourceDocEntry | undefined) => SourceDocEntry,
): void {
  documents.update(session, (previous) => ({
    ...previous,
    [target]: update(previous[target]),
  }));
}

export function sourceDoc(
  session: ClientSession | null,
  target: string,
): SourceDocEntry | undefined {
  return documents.get(session)?.[target];
}

export function selectedSourceTokenPrompt(): SourceTokenPrompt | null {
  return tokenPrompts.get(selectedSession()) ?? null;
}

export function dismissSourceTokenPrompt(session: ClientSession): void {
  tokenPrompts.update(session, () => null);
  session.feature("sources").publish("dismissToken", {});
}

export function onSourceEditError(listener: (error: SourceEditError) => void): () => void {
  editErrorListeners.add(listener);
  return () => editErrorListeners.delete(listener);
}

export function openSourceTarget(session: ClientSession, url: string): void {
  session.feature("sources").publish("open", { url });
}

export function openSelectedSourceTarget(url: string): void {
  const session = selectedSession();
  if (session !== null) {
    openSourceTarget(session, url);
  }
}

export function saveSourceEdit(
  session: ClientSession,
  target: string,
  oldText: string,
  newText: string,
): void {
  session.feature("sources").publish("saveEdit", { target, oldText, newText });
}

export function submitSourceToken(
  session: ClientSession,
  sourceId: string,
  token: string,
): Promise<{ ok: boolean; error: string }> {
  return session.feature("sources").request("saveToken", { sourceId, token });
}

registerSessionFeature((session) => {
  const source = session.feature("sources");
  const offPrompt = source.on<{ sourceId: string; label: string }>(
    "promptToken",
    ({ sourceId, label }) => {
      tokenPrompts.update(session, () => ({ session, sourceId, label }));
    },
  );
  const offLoading = source.on<{
    target: string;
    title: string;
    sourceId: string;
  }>("loading", ({ target, title, sourceId }) => {
    updateDocument(session, target, () => ({
      title,
      sourceId,
      editedTime: "",
      truncated: false,
      unknownBlocks: 0,
      status: "loading",
    }));
  });
  const offDocument = source.on<{
    target: string;
    title: string;
    sourceId: string;
    markdown?: string;
    html?: string;
    editedTime: string;
    truncated?: boolean;
    unknownBlocks?: number;
  }>("document", (message) => {
    updateDocument(session, message.target, () => ({
      title: message.title,
      sourceId: message.sourceId,
      ...(message.markdown === undefined ? {} : { markdown: message.markdown }),
      ...(message.html === undefined ? {} : { html: message.html }),
      editedTime: message.editedTime,
      truncated: message.truncated === true,
      unknownBlocks: message.unknownBlocks ?? 0,
      status: "ready",
    }));
  });
  const offError = source.on<{ target: string; message: string }>(
    "error",
    ({ target, message }) => {
      updateDocument(session, target, (previous) => ({
        title: previous?.title ?? "Notion",
        sourceId: previous?.sourceId ?? "",
        editedTime: "",
        truncated: false,
        unknownBlocks: 0,
        status: "error",
        message,
      }));
    },
  );
  const offEditError = source.on<{
    target: string;
    message: string;
    stale: boolean;
  }>("editError", ({ target, message, stale }) => {
    failSourceEdit(session, target, { message, stale });
    for (const listener of editErrorListeners) {
      listener({ session, target, message, stale });
    }
  });
  return () => {
    offPrompt();
    offLoading();
    offDocument();
    offError();
    offEditError();
  };
});
