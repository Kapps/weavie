import { expect, it, vi } from "vitest";
import type { ClientSession } from "../../bridge";

const harness = vi.hoisted(() => ({
  installer: undefined as ((session: ClientSession) => undefined | (() => void)) | undefined,
  selected: null as ClientSession | null,
  handlers: new Map<string, (payload: unknown) => void>(),
  request: vi.fn(),
  posted: [] as Array<{ name: string; payload: unknown }>,
}));

vi.mock("../../bridge", () => ({
  registerSessionFeature: (
    installer: (session: ClientSession) => undefined | (() => void),
  ): (() => void) => {
    harness.installer = installer;
    return () => {};
  },
  selectedSession: () => harness.selected,
}));

const store = await import("./source-store");

function sessionForTest(): ClientSession {
  return {
    feature: () => ({
      on: (name: string, handler: (payload: unknown) => void) => {
        harness.handlers.set(name, handler);
        return () => harness.handlers.delete(name);
      },
      request: harness.request,
      publish: (name: string, payload: unknown) => harness.posted.push({ name, payload }),
    }),
  } as unknown as ClientSession;
}

it("dismisses a token prompt in its owning session and clears the retained host state", () => {
  const session = sessionForTest();
  harness.selected = session;
  const cleanup = harness.installer?.(session);
  harness.handlers.get("promptToken")?.({ sourceId: "notion", label: "Notion" });
  expect(store.selectedSourceTokenPrompt()?.session).toBe(session);

  store.dismissSourceTokenPrompt(session);

  expect(store.selectedSourceTokenPrompt()).toBeNull();
  expect(harness.posted).toEqual([{ name: "dismissToken", payload: {} }]);
  cleanup?.();
});

it("rejects replayed snapshots and refreshes invalidated by a cancelled edit", async () => {
  const session = sessionForTest();
  const cleanup = harness.installer?.(session);
  const target = "https://notion.so/doc";
  const doc = {
    title: "Doc",
    markdown: "Original",
    editedTime: "",
    truncated: false,
    unknownBlocks: 0,
  };
  const snapshot = { ...doc, target, sourceId: "notion", editId: "", revision: 1 };
  const receive = harness.handlers.get("document")!;
  const signal = new AbortController().signal;
  receive(snapshot);
  const original = store.sourceDoc(session, target);
  harness.request.mockResolvedValueOnce(doc);
  await store.refreshSourceDoc(session, target, signal);
  expect(store.sourceDoc(session, target)).toBe(original);
  harness.request.mockResolvedValueOnce({ ...doc, markdown: "Remote change" });
  await store.refreshSourceDoc(session, target, signal);
  receive(snapshot);
  expect(store.sourceDoc(session, target)?.markdown).toBe("Remote change");

  let resolve!: (value: typeof doc) => void;
  harness.request.mockReturnValueOnce(
    new Promise((done) => {
      resolve = done;
    }),
  );
  const pending = store.refreshSourceDoc(session, target, signal);
  store.keepSourceEdit(session, target, {
    id: "draft",
    markdown: "Remote change",
    line: 0,
    original: "Remote change",
    draft: "Local draft",
    saving: false,
    error: undefined,
  });
  store.discardSourceEdit(session, target);
  resolve({ ...doc, markdown: "Outdated refresh" });
  await pending;
  expect(store.sourceDoc(session, target)?.markdown).toBe("Remote change");
  cleanup?.();
});
