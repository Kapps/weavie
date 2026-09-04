import { expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";

vi.mock("solid-js", () => import(["solid-js", "dist/solid.js"].join("/")));

let install!: (session: ClientSession) => () => void;
vi.mock("../bridge", () => ({
  registerSessionFeature: (installer: typeof install) => {
    install = installer;
  },
}));

const { createSessionOwnedResource } = await import("./session-owned-state");

it("owns state and cleanup by exact session incarnation", () => {
  const released: string[] = [];
  const store = createSessionOwnedResource(
    (session) => session.address.incarnation,
    (_session, state) => released.push(state),
  );
  const first = { address: { incarnation: "first" }, closed: false } as ClientSession;
  const replacement = { address: { incarnation: "replacement" }, closed: false } as ClientSession;
  const closeFirst = install(first);
  install(replacement);

  store.update(first, () => "changed");
  expect(store.get(replacement)).toBe("replacement");

  Object.defineProperty(first, "closed", { value: true });
  closeFirst();
  store.update(first, () => "resurrected");
  expect(store.get(first)).toBeUndefined();
  expect(store.get(replacement)).toBe("replacement");
  expect(released).toEqual(["changed"]);
});
