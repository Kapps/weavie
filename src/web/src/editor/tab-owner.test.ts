import { expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";
import type { TabPresenter } from "./tab-owner";
import { TabOwner } from "./tab-owner";

const owner = () =>
  new TabOwner({ signal: new AbortController().signal } as ClientSession, {
    path: "weavie:review",
    kind: "review",
    viewState: null,
  });
const presenter = (): Omit<TabPresenter, "signal"> => ({
  text: true,
  capture: () => ({ state: null, text: null }),
  restore: async () => {},
  focus: vi.fn(),
  actions: () => undefined,
});

it("waits for the exact tab and retires only the disposed mounting", async () => {
  const a = owner();
  const b = owner();
  const pending = a.wait(a.signal);
  const other = presenter();
  b.mount(other);
  const first = presenter();
  const unmount = a.mount(first);
  const mounted = await pending;
  expect(a.presentation).toBe(mounted);
  expect(mounted.focus).toBe(first.focus);
  unmount();
  const next = presenter();
  a.mount(next);
  unmount();
  expect(mounted.signal.aborted).toBe(true);
  expect(a.presentation?.focus).toBe(next.focus);
});

it("closing and reopening the same resource cannot complete a retired owner's wait", async () => {
  const original = owner();
  const pending = original.wait(original.signal);
  original.dispose();
  owner().mount(presenter());
  await expect(pending).rejects.toThrow("cancelled");
});

it("mount failures reject existing and later waiters", async () => {
  const tab = owner();
  const pending = tab.wait(tab.signal);
  tab.failed(new Error("Cannot load this renderer"));
  await expect(pending).rejects.toThrow("Cannot load");
  await expect(tab.wait(tab.signal)).rejects.toThrow("Cannot load");
});
