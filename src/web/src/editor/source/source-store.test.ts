import { expect, it, vi } from "vitest";
import type { ClientSession } from "../../bridge";

const harness = vi.hoisted(() => ({
  installer: undefined as ((session: ClientSession) => undefined | (() => void)) | undefined,
  selected: null as ClientSession | null,
  handlers: new Map<string, (payload: unknown) => void>(),
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

it("dismisses a token prompt in its owning session and clears the retained host state", () => {
  const session = {
    feature: () => ({
      on: (name: string, handler: (payload: unknown) => void) => {
        harness.handlers.set(name, handler);
        return () => harness.handlers.delete(name);
      },
      publish: (name: string, payload: unknown) => harness.posted.push({ name, payload }),
    }),
  } as unknown as ClientSession;
  harness.selected = session;
  const cleanup = harness.installer?.(session);
  harness.handlers.get("promptToken")?.({ sourceId: "notion", label: "Notion" });
  expect(store.selectedSourceTokenPrompt()?.session).toBe(session);

  store.dismissSourceTokenPrompt(session);

  expect(store.selectedSourceTokenPrompt()).toBeNull();
  expect(harness.posted).toEqual([{ name: "dismissToken", payload: {} }]);
  cleanup?.();
});
