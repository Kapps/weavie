import { beforeEach, expect, it, vi } from "vitest";
import type { ClientSession } from "../../bridge";

const harness = vi.hoisted(() => ({
  installer: undefined as ((session: ClientSession) => unknown) | undefined,
  handlers: new Map<string, (payload: unknown) => void>(),
  request: vi.fn(),
}));
vi.mock("../../bridge", () => ({
  registerSessionFeature: (installer: (session: ClientSession) => unknown) => {
    harness.installer = installer;
    return () => {};
  },
  selectedSession: () => null,
}));
const store = await import("./source-store");
const target = "https://notion.so/doc";
const initial = {
  target,
  title: "Doc",
  markdown: "Original",
  sourceId: "notion",
  editedTime: "",
  truncated: false,
  unknownBlocks: 0,
  editId: "",
};
let session: ClientSession;
let signal: AbortSignal;
let revision = 0;
function emit(name: string, payload: unknown): void {
  harness.handlers.get(name)?.(
    name === "document" ? { ...(payload as object), revision: ++revision } : payload,
  );
}
function beginEdit() {
  const edit = {
    id: crypto.randomUUID(),
    markdown: "Original",
    line: 0,
    original: "Original",
    draft: "My unfinished edit",
    saving: false,
    error: undefined,
  };
  store.keepSourceEdit(session, target, edit);
  return edit;
}
function pendingFetch() {
  let resolve!: (doc: typeof initial) => void;
  harness.request.mockImplementationOnce(
    () =>
      new Promise((done) => {
        resolve = done;
      }),
  );
  const task = store.refreshSourceDoc(session, target, signal);
  return { task, resolve };
}
beforeEach(() => {
  harness.request.mockReset();
  harness.handlers.clear();
  session = {
    closed: false,
    feature: () => ({
      on: (name: string, handler: (payload: unknown) => void) => {
        harness.handlers.set(name, handler);
        return () => harness.handlers.delete(name);
      },
      request: harness.request,
      publish: vi.fn(),
    }),
  } as unknown as ClientSession;
  signal = new AbortController().signal;
  harness.installer?.(session);
  emit("document", initial);
});

it("refreshes remote changes without replacing an unchanged document", async () => {
  const before = store.sourceDoc(session, target);
  const { target: _, sourceId: __, editId: ___, ...doc } = initial;
  harness.request.mockResolvedValueOnce(doc);
  await store.refreshSourceDoc(session, target, signal);
  expect(store.sourceDoc(session, target)).toBe(before);
  harness.request.mockResolvedValueOnce({ ...doc, markdown: "Remote change", title: "New title" });
  await store.refreshSourceDoc(session, target, signal);
  expect(store.sourceDoc(session, target)).toMatchObject({
    markdown: "Remote change",
    title: "New title",
  });
});

it("keeps drafts through all unsolicited loading, document and error messages", async () => {
  const before = store.sourceDoc(session, target);
  const edit = beginEdit();
  emit("loading", initial);
  emit("document", { ...initial, markdown: "Remote change" });
  emit("error", { target, message: "Network failure" });
  await store.refreshSourceDoc(session, target, signal);
  expect(harness.request).not.toHaveBeenCalled();
  expect(store.sourceDoc(session, target)).toBe(before);
  expect(store.sourceEditState(session, target)).toBe(edit);
});

it.each([
  false,
  true,
])("rejects a refresh if editing began during the fetch (cancelled: %s)", async (cancelled) => {
  const before = store.sourceDoc(session, target);
  const fetch = pendingFetch();
  const edit = beginEdit();
  if (cancelled) store.discardSourceEdit(session, target);
  fetch.resolve({ ...initial, markdown: "Remote change" });
  await fetch.task;
  expect(store.sourceDoc(session, target)).toBe(before);
  expect(store.sourceEditState(session, target)).toBe(cancelled ? undefined : edit);
});

it("only a matching save acknowledgement closes the draft, even for identical markdown", () => {
  const edit = beginEdit();
  edit.saving = true;
  emit("document", { ...initial, markdown: "Another result", editId: "other-save" });
  emit("editError", { target, message: "Old error", stale: true, editId: "other-save" });
  expect(store.sourceEditState(session, target)).toBe(edit);
  expect(edit.saving).toBe(true);
  emit("document", { ...initial, editId: edit.id });
  expect(store.sourceEditState(session, target)).toBeUndefined();
});

it("never lets a pre-save refresh replace the successful save", async () => {
  const fetch = pendingFetch();
  const edit = beginEdit();
  edit.saving = true;
  emit("document", { ...initial, markdown: "Saved", editId: edit.id });
  fetch.resolve({ ...initial, markdown: "Old remote value" });
  await fetch.task;
  expect(store.sourceDoc(session, target)?.markdown).toBe("Saved");
});

it("retains a failed save and ignores refreshes while it awaits user action", async () => {
  const edit = beginEdit();
  edit.saving = true;
  emit("editError", { target, message: "Conflict", stale: true, editId: edit.id });
  emit("document", { ...initial, markdown: "Remote change" });
  await store.refreshSourceDoc(session, target, signal);
  expect(store.sourceEditState(session, target)).toMatchObject({
    draft: "My unfinished edit",
    saving: false,
    error: { message: "Conflict", stale: true },
  });
  expect(harness.request).not.toHaveBeenCalled();
});

it("shows refresh failures alongside the retained body, and clears them on recovery", async () => {
  harness.request.mockRejectedValueOnce(new Error("Notion unavailable"));
  await store.refreshSourceDoc(session, target, signal);
  expect(store.sourceDoc(session, target)).toMatchObject({
    status: "ready",
    markdown: "Original",
    refreshError: "Notion unavailable",
  });
  harness.request.mockResolvedValueOnce(initial);
  await store.refreshSourceDoc(session, target, signal);
  expect(store.sourceDoc(session, target)?.refreshError).toBeUndefined();
});

it("discards a refresh after the view lifetime ends", async () => {
  const lifetime = new AbortController();
  signal = lifetime.signal;
  const fetch = pendingFetch();
  lifetime.abort();
  fetch.resolve({ ...initial, markdown: "Remote change" });
  await fetch.task;
  expect(store.sourceDoc(session, target)?.markdown).toBe("Original");
});

it("does not replay an older host snapshot over accepted refresh content", async () => {
  const lastRevision = revision;
  harness.request.mockResolvedValueOnce({ ...initial, markdown: "Fresh remote content" });
  await store.refreshSourceDoc(session, target, signal);
  harness.handlers.get("document")?.({ ...initial, revision: lastRevision });
  expect(store.sourceDoc(session, target)?.markdown).toBe("Fresh remote content");
  const edit = beginEdit();
  edit.saving = true;
  emit("document", { ...initial, markdown: "Saved", editId: edit.id });
  expect(store.sourceEditState(session, target)).toBeUndefined();
  expect(store.sourceDoc(session, target)?.markdown).toBe("Saved");
});
