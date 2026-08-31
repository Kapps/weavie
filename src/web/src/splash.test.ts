import { beforeEach, expect, it, vi } from "vitest";

beforeEach(() => {
  vi.resetModules();
});

it("notifies splash listeners exactly once when the splash is removed", async () => {
  let present = true;
  const remove = vi.fn(() => {
    present = false;
  });
  vi.stubGlobal("document", {
    getElementById: () => (present ? { remove } : null),
  });
  const { dismissSplash, onSplashDismissed } = await import("./splash");
  const listener = vi.fn();
  onSplashDismissed(listener);

  dismissSplash();
  dismissSplash();

  expect(remove).toHaveBeenCalledOnce();
  expect(listener).toHaveBeenCalledOnce();
});

it("runs a late listener immediately after the splash is gone", async () => {
  vi.stubGlobal("document", { getElementById: () => null });
  const { onSplashDismissed } = await import("./splash");
  const listener = vi.fn();

  onSplashDismissed(listener);

  expect(listener).toHaveBeenCalledOnce();
});
