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

const { listSelectedDirectory, selectedDirectoryListings } = await import("./session-files");

function owner() {
  const handlers = new Map<string, (message: unknown) => void>();
  const pending: ReturnType<typeof Promise.withResolvers<{ entries: DirEntry[] }>>[] = [];
  const feature = {
    on: (name: string, receive: (message: unknown) => void) => {
      handlers.set(name, receive);
      return () => handlers.delete(name);
    },
    request: vi.fn(() => {
      const request = Promise.withResolvers<{ entries: DirEntry[] }>();
      pending.push(request);
      return request.promise;
    }),
  };
  const session = { closed: false, feature: () => feature } as unknown as ClientSession;
  const cleanup = installers.map((install) => install(session));
  return {
    session,
    pending,
    feature,
    invalidate: () => handlers.get("changed")?.({ changes: [{ path: "/repo" }] }),
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
  listSelectedDirectory("/repo/docs");
  first.pending[0]!.resolve({ entries: [folder] });
  await first.pending[0]!.promise;
  expect(selectedDirectoryListings()["/repo/docs"]).toEqual({ status: "ready", entries: [folder] });

  first.invalidate();
  first.invalidate();
  expect(first.feature.request).toHaveBeenCalledTimes(2);
  first.pending[1]!.resolve({ entries: [] });
  await first.pending[1]!.promise;
  expect(first.feature.request).toHaveBeenCalledTimes(3);
  expect(selectedDirectoryListings()["/repo/docs"]).toEqual({ status: "ready", entries: [folder] });
  first.pending[2]!.resolve({ entries: [{ ...folder }] });
  await first.pending[2]!.promise;
  const state = selectedDirectoryListings()["/repo/docs"];
  expect(state?.status === "ready" && state.entries[0] === folder).toBe(true);
  first.index(true);
  expect(first.feature.request).toHaveBeenCalledTimes(4);
  first.pending[3]!.resolve({ entries: [] });
  await first.pending[3]!.promise;
  expect(selectedDirectoryListings()["/repo/docs"]).toEqual({ status: "ready", entries: [] });
  first.close();
});

it("keeps pending replies with their owner and cannot repopulate a closed session", async () => {
  const first = owner();
  const second = owner();
  selected = first.session;
  listSelectedDirectory("/repo");
  first.invalidate();
  selected = second.session;
  first.close();
  first.pending[0]!.resolve({ entries: [{ path: "/repo/stale", name: "stale", isDir: false }] });
  await Promise.resolve();
  await Promise.resolve();
  expect(first.feature.request).toHaveBeenCalledTimes(1);
  expect(selectedDirectoryListings()).toEqual({});
  expect(second.feature.request).not.toHaveBeenCalled();
  second.close();
});
