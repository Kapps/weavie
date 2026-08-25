import { describe, expect, it } from "vitest";
import { needsAcknowledgement } from "./delete-session-confirm";

describe("needsAcknowledgement", () => {
  it("gates a checkout with no branch, whose commits the removal discards", () => {
    expect(needsAcknowledgement("clean", true)).toBe(true);
  });

  it("gates uncommitted changes, and leaves the recoverable states to their own confirm", () => {
    expect(needsAcknowledgement("modified", false)).toBe(true);
    expect(needsAcknowledgement("untracked", false)).toBe(false);
    expect(needsAcknowledgement("clean", false)).toBe(false);
  });
});
