import { createComputed, createRoot, onCleanup } from "solid-js";
import { expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";
import type { TabPresenter } from "./tab-owner";
import { TabOwner } from "./tab-owner";

vi.mock("solid-js", () => import(["solid-js", "dist/solid.js"].join("/")));

const owner = () =>
  new TabOwner(
    { signal: new AbortController().signal } as ClientSession,
    { path: "weavie:review", kind: "review" },
    null,
  );
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

it("publishes presenter ownership changes without publishing reading-state captures", () => {
  createRoot((dispose) => {
    const tab = owner();
    const states: (boolean | undefined)[] = [];
    createComputed(() => states.push(tab.presentation?.text));
    const removeSource = tab.mount(presenter());
    tab.saveViewState({ scrollTop: 100 });
    expect(states).toEqual([undefined, true]);
    removeSource();
    const removePreview = tab.mount({ ...presenter(), text: false });
    removeSource();
    expect(states).toEqual([undefined, true, undefined, false]);
    removePreview();
    expect(states).toEqual([undefined, true, undefined, false, undefined]);
    tab.mount(presenter());
    tab.dispose();
    expect(states).toEqual([undefined, true, undefined, false, undefined, true, undefined]);
    dispose();
  });
});

it("imperative mount and wait do not subscribe the effect that owns the presenter", () => {
  createRoot((dispose) => {
    const tab = owner();
    const mounted = vi.fn();
    let remove = () => {};
    createComputed(() => {
      mounted();
      remove = tab.mount(presenter());
      void tab.wait(tab.signal);
      onCleanup(remove);
    });
    expect(mounted).toHaveBeenCalledOnce();
    remove();
    expect(mounted).toHaveBeenCalledOnce();
    expect(tab.presentation).toBeUndefined();
    dispose();
  });
});

it("retires a presenter when a reactive consumer closes its owner during publication", async () => {
  const tab = owner();
  const pending = tab.wait(tab.signal);
  const content = presenter();
  createRoot((dispose) => {
    createComputed(() => {
      if (tab.presentation !== undefined) tab.dispose();
    });
    tab.mount(content);
    expect(tab.signal.aborted).toBe(true);
    expect(tab.presentation).toBeUndefined();
    dispose();
  });
  const mounted = await pending;
  expect(mounted.focus).toBe(content.focus);
  expect(mounted.signal.aborted).toBe(true);
});
