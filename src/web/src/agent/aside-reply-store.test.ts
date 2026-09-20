import { describe, expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";
import { asideReplyState, clearAsideReplyStates, setAsideReplyState } from "./aside-reply-store";

vi.mock("../bridge", () => ({ registerSessionFeature: () => () => {} }));

const session = (): ClientSession => ({}) as ClientSession;

describe("aside reply state", () => {
  it("belongs to the exact client-session incarnation and conversation", () => {
    const first = session();
    const nextIncarnation = session();

    setAsideReplyState(first, "aside-1", { open: true });

    expect(asideReplyState(first, "aside-1")).toEqual({ open: true });
    expect(asideReplyState(first, "aside-2")).toEqual({ open: false });
    expect(asideReplyState(nextIncarnation, "aside-1")).toEqual({ open: false });
  });

  it("drops settled empty state", () => {
    const current = session();
    setAsideReplyState(current, "aside-1", { open: true });
    const settled = { open: false };

    setAsideReplyState(current, "aside-1", settled);

    expect(asideReplyState(current, "aside-1")).toEqual({ open: false });
    expect(asideReplyState(current, "aside-1")).not.toBe(settled);
  });

  it("drops every reply with its conversation generation", () => {
    const current = session();
    setAsideReplyState(current, "aside-1", { open: true });
    setAsideReplyState(current, "aside-2", { open: false });

    clearAsideReplyStates(current);

    expect(asideReplyState(current, "aside-1")).toEqual({ open: false });
    expect(asideReplyState(current, "aside-2")).toEqual({ open: false });
  });
});
