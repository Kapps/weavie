import { createComputed, createRoot } from "solid-js";
import { beforeEach, expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";

vi.mock("solid-js", () => import(["solid-js", "dist/solid.js"].join("/")));
const notify = vi.hoisted(() => vi.fn());
vi.mock("../notify/notify", () => ({ notify }));

const { createOwnedSurface } = await import("./owned-surface");

const session = () => {
  const ended = new AbortController();
  return { client: { signal: ended.signal } as ClientSession, end: () => ended.abort() };
};

beforeEach(() => notify.mockClear());

it("closes, saying why, when its session ends while it is open", () => {
  const surface = createOwnedSurface("Open URL");
  const a = session();
  surface.open(a.client);
  expect(surface.owner()).toBe(a.client);

  a.end();
  expect(surface.owner()).toBeNull();
  expect(notify).toHaveBeenCalledExactlyOnceWith("warn", "Open URL closed: its session ended.");
});

it("stays as it is when opened again for its own session", () => {
  const surface = createOwnedSurface("Open URL");
  const a = session();
  const owners: (ClientSession | null)[] = [];
  createRoot(() => createComputed(() => owners.push(surface.owner())));
  surface.open(a.client);
  surface.open(a.client);
  expect(owners).toEqual([null, a.client]);
});

it("moves to a live session and no longer answers to the one it left", () => {
  const surface = createOwnedSurface("Diff Against");
  const a = session();
  const b = session();
  surface.open(a.client);
  surface.open(b.client);
  a.end();
  expect(surface.owner()).toBe(b.client);
  expect(notify).not.toHaveBeenCalled();
});

it("refuses an ended session without closing the one that is open", () => {
  const surface = createOwnedSurface("Open URL");
  const a = session();
  const b = session();
  a.end();
  surface.open(b.client);
  expect(() => surface.open(a.client)).toThrow("Open URL can't open: its session ended.");
  expect(surface.owner()).toBe(b.client);
});
