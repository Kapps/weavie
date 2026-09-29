import { createSignal } from "solid-js";
import { expect, it, vi } from "vitest";
import type { ClientSession, HostConnection } from "../bridge";
import { ownFileNavigation } from "./reveal";

const env = vi.hoisted(() => ({
  selected: () => null as ClientSession | null,
  select: (_session: ClientSession) => {},
  install: undefined as ((connection: HostConnection) => void) | undefined,
  session: undefined as ClientSession | undefined,
}));
vi.mock("solid-js", () => import(["solid-js", "dist/solid.js"].join("/")));
vi.mock("../bridge", () => ({
  selectedSession: () => env.selected(),
  selectClientSession: (session: ClientSession) => env.select(session),
  sessionForSlot: () => env.session,
  registerHostFeature: (install: (connection: HostConnection) => void) => {
    env.install = install;
  },
}));

it("drains every cold-launch OS path as one durable batch, never as superseding reveals", async () => {
  const [selected, select] = createSignal<ClientSession | null>(null);
  env.selected = selected;
  env.select = select;
  const session = { connection: { id: "local" }, address: { slot: "workspace" } } as ClientSession;
  env.session = session;
  const reveal = vi.fn(async () => {});
  const openFiles = vi.fn(async () => {});
  const release = ownFileNavigation(session, { reveal, openFiles });
  await import("./open-path");
  let deliver!: (message: { path: string; fallbackSlot: string }) => void;
  env.install!({
    id: "local",
    host: {
      feature: () => ({
        on: (_name: string, handler: typeof deliver) => {
          deliver = handler;
        },
      }),
    },
  } as unknown as HostConnection);
  deliver({ path: "/one.ts", fallbackSlot: "workspace" });
  deliver({ path: "/two.ts", fallbackSlot: "workspace" });
  expect(openFiles).not.toHaveBeenCalled();
  select(session);
  expect(openFiles).toHaveBeenCalledExactlyOnceWith(["/one.ts", "/two.ts"]);
  expect(reveal).not.toHaveBeenCalled();
  release();
});
