import { createRoot, createSignal } from "solid-js";
import { expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";

vi.mock("solid-js", () => import(["solid-js", "dist/solid.js"].join("/")));

const { createSessionsOpening } = await import("./sessions-opening");

const a = { id: "a" } as unknown as ClientSession;
const b = { id: "b" } as unknown as ClientSession;

it("holds the session Sessions was opened from until it opens again", () => {
  const [active, setActive] = createSignal(false);
  const [location, setLocation] = createSignal("local");
  const [shown, setShown] = createSignal<ClientSession | null>(a);
  const opening = createRoot(() => createSessionsOpening(active, location, shown));

  setActive(true);
  const first = opening();
  expect(first).toEqual({ owner: a, backendId: "local" });

  setShown(b);
  expect(opening()).toBe(first);

  setLocation("remote:devbox");
  expect(opening()).toEqual({ owner: b, backendId: "remote:devbox" });

  setActive(false);
  const closed = opening();
  setShown(a);
  setLocation("local");
  expect(opening()).toBe(closed);

  setActive(true);
  expect(opening()).toEqual({ owner: a, backendId: "local" });
});
