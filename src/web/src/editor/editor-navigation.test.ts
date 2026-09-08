import { afterEach, expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";
import { createEditorNavigation } from "./editor-navigation";
import type { NavLocation } from "./nav-history";

afterEach(() => vi.useRealTimers());

it("an explicit same-file jump discards cursor snapshots captured before its reveal settled", async () => {
  vi.useFakeTimers();
  const owner = { signal: new AbortController().signal } as ClientSession;
  const origin: NavLocation = {
    kind: "review",
    path: "/review.ts",
    line: 80,
    anchor: { line: 80, offset: 0 },
  };
  const intermediate: NavLocation = { ...origin, anchor: { line: 80, offset: 4 } };
  const destination: NavLocation = { ...origin, line: 1, anchor: { line: 1, offset: 0 } };
  const restore = vi.fn(async () => {});
  const navigation = createEditorNavigation({
    capture: () => destination,
    restore,
    changed: () => {},
    failed: () => {},
  });
  navigation.record(owner, origin);
  navigation.schedule(owner, intermediate, owner.signal);
  navigation.push(owner, destination);
  await vi.runAllTimersAsync();
  navigation.capture(owner);
  navigation.history(owner).back();
  expect(restore).toHaveBeenLastCalledWith(owner, origin, expect.any(AbortSignal));
  navigation.dispose();
});

function fixture() {
  const owner = { signal: new AbortController().signal } as ClientSession;
  const origin: NavLocation = { kind: "review", path: "/review.ts", line: 80 };
  const destination: NavLocation = { kind: "file", path: "/definition.ts", line: 1 };
  let release!: () => void;
  const delayed = new Promise<void>((resolve) => {
    release = resolve;
  });
  const restored: NavLocation[] = [];
  const navigation = createEditorNavigation({
    capture: () => destination,
    restore: async (_session, location) => {
      restored.push(location);
      await delayed;
    },
    changed: () => {},
    failed: () => {},
  });
  navigation.record(owner, origin);
  navigation.record(owner, destination);
  return { owner, origin, destination, navigation, restored, release };
}

it("cursor events emitted during restoration cannot truncate Forward after their debounce expires", async () => {
  vi.useFakeTimers();
  const { owner, navigation, release } = fixture();
  const history = navigation.history(owner);
  history.back();
  navigation.schedule(owner, { kind: "review", path: "/review.ts", line: 1 }, owner.signal);
  release();
  await vi.runAllTimersAsync();
  expect(history.canForward()).toBe(true);
  navigation.dispose();
});

it("superseding a delayed restore releases recording immediately and cancels its file operation", async () => {
  const { owner, navigation, restored, release, destination } = fixture();
  const history = navigation.history(owner);
  history.back();
  const superseded = navigation.signal(owner);
  navigation.depart(owner);
  expect(superseded.aborted).toBe(true);
  expect(history.canRecord()).toBe(true);
  navigation.record(owner, { kind: "file", path: "/new.ts", line: 20 });
  release();
  await Promise.resolve();
  await Promise.resolve();
  expect(history.back()).toBe(true);
  expect(restored.at(-1)).toEqual(destination);
  navigation.dispose();
});

it("a session's departure never invalidates another owner's navigation operation", () => {
  const { owner, navigation } = fixture();
  const other = { signal: new AbortController().signal } as ClientSession;
  const otherSignal = navigation.signal(other);
  navigation.depart(owner);
  expect(otherSignal.aborted).toBe(false);
  navigation.dispose();
  expect(otherSignal.aborted).toBe(true);
});
