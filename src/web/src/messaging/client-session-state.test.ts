import { expect, it } from "vitest";
import { ClientSessionState } from "./client-session-state";
import { MessageBus } from "./message-bus";

it("restores review metadata alongside normalized editor tabs", async () => {
  const address = { slot: "main", incarnation: "restored" };
  const bus = new MessageBus("session", address, () => {});
  const state = new ClientSessionState(bus);
  const restored = Promise.withResolvers<void>();
  const unsubscribe = state.editor.subscribe((value) => {
    if (value !== null) restored.resolve();
  });
  const review = { mode: "unified", cursor: { path: "/w/file", line: 17 }, files: {} };
  bus.receive({
    scope: "session",
    session: address,
    kind: "event",
    requestId: null,
    feature: "editor",
    name: "restore",
    error: null,
    payload: {
      session: {
        active: "/w/file",
        open: [{ path: "/w/file", kind: null, viewState: null }],
        review,
      },
    },
  });
  await restored.promise;
  expect(state.editor.current).toEqual({
    active: "/w/file",
    open: [{ path: "/w/file", viewState: null }],
    review,
  });
  unsubscribe();
  bus.close("Test complete");
});
