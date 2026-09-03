import { describe, expect, it } from "vitest";
import type { ClientSession } from "../bridge";
import { asideReplyState, clearAsideReplyStates, setAsideReplyState } from "./aside-reply-store";

const session = (): ClientSession => ({}) as ClientSession;

describe("aside reply state", () => {
  it("belongs to the exact client-session incarnation and conversation", () => {
    const first = session();
    const nextIncarnation = session();

    setAsideReplyState(first, "aside-1", { draft: "unfinished", open: true });

    expect(asideReplyState(first, "aside-1")).toEqual({ draft: "unfinished", open: true });
    expect(asideReplyState(first, "aside-2")).toEqual({ draft: "", open: false });
    expect(asideReplyState(nextIncarnation, "aside-1")).toEqual({ draft: "", open: false });
  });

  it("drops settled empty state", () => {
    const current = session();
    setAsideReplyState(current, "aside-1", { draft: "send me", open: true });
    const settled = { draft: "", open: false };

    setAsideReplyState(current, "aside-1", settled);

    expect(asideReplyState(current, "aside-1")).toEqual({ draft: "", open: false });
    expect(asideReplyState(current, "aside-1")).not.toBe(settled);
  });

  it("drops every draft with its conversation generation", () => {
    const current = session();
    setAsideReplyState(current, "aside-1", { draft: "first", open: true });
    setAsideReplyState(current, "aside-2", { draft: "second", open: false });

    clearAsideReplyStates(current);

    expect(asideReplyState(current, "aside-1")).toEqual({ draft: "", open: false });
    expect(asideReplyState(current, "aside-2")).toEqual({ draft: "", open: false });
  });
});
