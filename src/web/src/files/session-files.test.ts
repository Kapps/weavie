import { expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";
import type { DirEntry } from "./FileBrowser";

vi.mock("solid-js", () => import(["solid-js", "dist/solid.js"].join("/")));
const installers: ((session: ClientSession) => () => void)[] = [];
let selected: ClientSession | null = null;
vi.mock("../bridge", () => ({
  registerSessionFeature: (install: (typeof installers)[number]) => installers.push(install),
  selectedSession: () => selected,
}));

const { acquireDirectory, selectedDirectoryListings } = await import("./session-files");

function owner() {
  const handlers = new Map<string, (message: unknown) => void>();
  const pending: ReturnType<typeof Promise.withResolvers<{ path: string; entries: DirEntry[] }>>[] =
    [];
  const feature = {
    publish: vi.fn(),
    on: (name: string, receive: (message: unknown) => void) => {
      handlers.set(name, receive);
      return () => handlers.delete(name);
    },
    request: vi.fn((_name: string, _payload: { path: string; subscriptionId: string }) => {
      const request = Promise.withResolvers<{ path: string; entries: DirEntry[] }>();
      pending.push(request);
      return request.promise;
    }),
  };
  const session = { closed: false, feature: () => feature } as unknown as ClientSession;
  const cleanup = installers.map((install) => install(session));
  expect(feature.publish).toHaveBeenCalledExactlyOnceWith("reset", {
    pageEpoch: expect.any(String),
  });
  feature.publish.mockClear();
  return {
    session,
    pending,
    feature,
    invalidate: () => handlers.get("changed")?.({ changes: [{ path: "/repo" }] }),
    changed: (path: string) => handlers.get("changed")?.({ changes: [{ path }] }),
    index: (pending: boolean) => handlers.get("index")?.({ root: "/repo", files: [], pending }),
    close: () => {
      Object.defineProperty(session, "closed", { value: true });
      for (const release of cleanup) release();
    },
  };
}

it("retains row identity and follows ancestor invalidation during a listing", async () => {
  const first = owner();
  selected = first.session;
  first.index(false);
  const folder: DirEntry = { path: "/repo/docs/nested", name: "nested", isDir: true };
  const listing = acquireDirectory(first.session, "/repo/docs");
  first.pending[0]!.resolve({ path: "/repo/docs", entries: [folder] });
  await first.pending[0]!.promise;
  expect(selectedDirectoryListings()["/repo/docs"]).toEqual({ status: "ready", entries: [folder] });

  first.invalidate();
  first.invalidate();
  expect(first.feature.request).toHaveBeenCalledTimes(2);
  first.pending[1]!.resolve({ path: "/repo/docs", entries: [] });
  await first.pending[1]!.promise;
  expect(first.feature.request).toHaveBeenCalledTimes(3);
  expect(selectedDirectoryListings()["/repo/docs"]).toEqual({ status: "ready", entries: [folder] });
  first.pending[2]!.resolve({ path: "/repo/docs", entries: [{ ...folder }] });
  await first.pending[2]!.promise;
  const state = selectedDirectoryListings()["/repo/docs"];
  expect(state?.status === "ready" && state.entries[0] === folder).toBe(true);
  first.index(true);
  expect(first.feature.request).toHaveBeenCalledTimes(4);
  first.pending[3]!.resolve({ path: "/repo/docs", entries: [] });
  await first.pending[3]!.promise;
  expect(selectedDirectoryListings()["/repo/docs"]).toEqual({ status: "ready", entries: [] });
  listing.release();
  first.close();
});

it("releases the captured owner when selection changes to a session with the same path", async () => {
  const first = owner();
  const second = owner();
  selected = first.session;
  const firstListing = acquireDirectory(first.session, "/repo");
  selected = second.session;
  const secondListing = acquireDirectory(second.session, "/repo");
  second.pending[0]!.resolve({ path: "/repo", entries: [] });
  await second.pending[0]!.promise;

  firstListing.release();
  expect(first.feature.publish).toHaveBeenCalledExactlyOnceWith("unwatchDirectory", {
    subscriptionId: first.feature.request.mock.calls[0]![1].subscriptionId,
  });
  expect(second.feature.publish).not.toHaveBeenCalled();
  expect(selectedDirectoryListings()["/repo"]).toEqual({ status: "ready", entries: [] });
  selected = first.session;
  expect(selectedDirectoryListings()).toEqual({});

  first.pending[0]!.resolve({ path: "/repo", entries: [] });
  await first.pending[0]!.promise;
  expect(selectedDirectoryListings()).toEqual({});
  secondListing.release();
  first.close();
  second.close();
});

it("shares pending and cached listings until the last consumer releases", async () => {
  const first = owner();
  selected = first.session;
  const browser = acquireDirectory(first.session, "/repo");
  const pendingConsumer = acquireDirectory(first.session, "/repo");
  expect(first.feature.request).toHaveBeenCalledTimes(1);
  first.pending[0]!.resolve({ path: "/repo", entries: [] });
  await first.pending[0]!.promise;
  const cachedConsumer = acquireDirectory(first.session, "/repo");
  expect(first.feature.request).toHaveBeenCalledTimes(1);

  browser.release();
  browser.release();
  pendingConsumer.release();
  expect(first.feature.publish).not.toHaveBeenCalled();
  expect(selectedDirectoryListings()["/repo"]).toEqual({ status: "ready", entries: [] });
  cachedConsumer.refresh();
  expect(first.feature.request).toHaveBeenCalledTimes(2);
  first.pending[1]!.resolve({ path: "/repo", entries: [] });
  await first.pending[1]!.promise;

  cachedConsumer.release();
  cachedConsumer.release();
  browser.refresh();
  cachedConsumer.refresh();
  first.invalidate();
  expect(first.feature.publish).toHaveBeenCalledExactlyOnceWith("unwatchDirectory", {
    subscriptionId: first.feature.request.mock.calls[0]![1].subscriptionId,
  });
  expect(first.feature.request).toHaveBeenCalledTimes(2);
  expect(selectedDirectoryListings()).toEqual({});
  first.close();
});

it.each([
  "success",
  "error",
])("ignores late %s after the last consumer releases", async (outcome) => {
  const first = owner();
  selected = first.session;
  const listing = acquireDirectory(first.session, "/repo");
  listing.release();
  if (outcome === "success") first.pending[0]!.resolve({ path: "/repo", entries: [] });
  else first.pending[0]!.reject(new Error("retired listing"));
  await first.pending[0]!.promise.catch(() => {});

  expect(selectedDirectoryListings()).toEqual({});
  expect(first.feature.request).toHaveBeenCalledTimes(1);
  first.close();
});

it.each([
  "success",
  "error",
])("does not retry queued invalidation after release and late %s", async (outcome) => {
  const first = owner();
  selected = first.session;
  const listing = acquireDirectory(first.session, "/repo");
  first.invalidate();
  first.invalidate();
  listing.release();
  if (outcome === "success") first.pending[0]!.resolve({ path: "/repo", entries: [] });
  else first.pending[0]!.reject(new Error("retired listing"));
  await first.pending[0]!.promise.catch(() => {});

  expect(first.feature.request).toHaveBeenCalledTimes(1);
  expect(selectedDirectoryListings()).toEqual({});
  first.close();
});

it.each([
  "success",
  "error",
])("keeps a reacquired listing pending when the retired request completes with %s", async (outcome) => {
  const first = owner();
  selected = first.session;
  const retired = acquireDirectory(first.session, "/repo");
  first.invalidate();
  retired.release();
  const current = acquireDirectory(first.session, "/repo");
  expect(first.feature.request.mock.calls[1]![1].subscriptionId).not.toBe(
    first.feature.request.mock.calls[0]![1].subscriptionId,
  );
  expect(first.feature.request).toHaveBeenCalledTimes(2);
  if (outcome === "success") {
    first.pending[0]!.resolve({
      path: "/repo",
      entries: [{ path: "/repo/stale", name: "stale", isDir: false }],
    });
  } else first.pending[0]!.reject(new Error("retired listing"));
  await first.pending[0]!.promise.catch(() => {});

  expect(selectedDirectoryListings()["/repo"]).toEqual({ status: "loading" });
  first.invalidate();
  retired.refresh();
  retired.release();
  expect(first.feature.request).toHaveBeenCalledTimes(2);
  expect(first.feature.publish).toHaveBeenCalledTimes(1);
  first.pending[1]!.resolve({ path: "/repo", entries: [] });
  await first.pending[1]!.promise;
  expect(first.feature.request).toHaveBeenCalledTimes(3);
  expect(first.feature.request.mock.calls[2]![1].subscriptionId).toBe(
    first.feature.request.mock.calls[1]![1].subscriptionId,
  );
  first.pending[2]!.resolve({ path: "/repo", entries: [] });
  await first.pending[2]!.promise;
  expect(selectedDirectoryListings()["/repo"]).toEqual({ status: "ready", entries: [] });
  current.release();
  expect(first.feature.publish).toHaveBeenCalledTimes(2);
  first.close();
});

it("keeps pending replies with their owner and cannot repopulate a closed session", async () => {
  const first = owner();
  const second = owner();
  selected = first.session;
  const listing = acquireDirectory(first.session, "/repo");
  first.invalidate();
  selected = second.session;
  first.close();
  first.pending[0]!.resolve({
    path: "/repo",
    entries: [{ path: "/repo/stale", name: "stale", isDir: false }],
  });
  await Promise.resolve();
  await Promise.resolve();
  expect(first.feature.request).toHaveBeenCalledTimes(1);
  expect(selectedDirectoryListings()).toEqual({});
  expect(second.feature.request).not.toHaveBeenCalled();
  listing.release();
  second.close();
});

it("refreshes an alias using the host's canonical directory path", async () => {
  const first = owner();
  selected = first.session;
  const listing = acquireDirectory(first.session, "/repo/./docs/");
  first.pending[0]!.resolve({ path: "/repo/docs", entries: [] });
  await first.pending[0]!.promise;
  first.changed("/repo/docs/new-child");
  expect(first.feature.request).toHaveBeenCalledTimes(2);
  listing.release();
  first.pending[1]!.resolve({ path: "/repo/docs", entries: [] });
  await first.pending[1]!.promise;
  first.close();
});
