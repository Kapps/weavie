import { describe, expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";

let install: (session: ClientSession) => () => void = () => () => {};
vi.mock("../bridge", () => ({
  LOCAL_BACKEND_ID: "local",
  registerSessionFeature: (installer: typeof install) => {
    install = installer;
  },
}));
const { runningBackgroundOf } = await import("./agent-background-store");

describe("agent background store", () => {
  it("tells a loaded session that has reported no work yet from one that is gone", () => {
    const address = { slot: "s1", incarnation: "i1" };
    const session = {
      connection: { id: "local" },
      address,
      feature: () => ({ on: () => () => {} }),
    } as unknown as ClientSession;
    const uninstall = install(session);
    expect(runningBackgroundOf("local", address)).toEqual([]);
    uninstall();
    expect(runningBackgroundOf("local", address)).toBeNull();
  });
});
