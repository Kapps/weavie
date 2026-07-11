import { describe, expect, it } from "vitest";
import type { AgentPaneUpdate } from "../bridge";
import { formatElapsed, hasActiveTurn, pendingRequest, pendingRequestKind } from "./turn-progress";

const message = (type: string, itemId?: string): AgentPaneUpdate => ({
  type,
  providerId: "codex",
  itemId: itemId ?? null,
});

describe("hasActiveTurn", () => {
  it("is false with no turn messages", () => {
    expect(hasActiveTurn([message("user-message")])).toBe(false);
  });

  it("is true after a start with no completion", () => {
    expect(hasActiveTurn([message("turn-started")])).toBe(true);
  });

  it("is false again after the turn completes or is interrupted", () => {
    expect(hasActiveTurn([message("turn-started"), message("turn-completed")])).toBe(false);
    expect(hasActiveTurn([message("turn-started"), message("turn-interrupted")])).toBe(false);
  });

  it("tracks the latest turn across several", () => {
    expect(
      hasActiveTurn([message("turn-started"), message("turn-completed"), message("turn-started")]),
    ).toBe(true);
  });
});

describe("pendingRequestKind", () => {
  it("is null with no unresolved requests", () => {
    expect(pendingRequestKind([message("turn-started")])).toBe(null);
  });

  it("reports the latest unresolved request kind", () => {
    expect(
      pendingRequestKind([
        message("turn-started"),
        message("approval-requested", "a1"),
        message("input-requested", "q1"),
      ]),
    ).toBe("input");
  });

  it("clears a request once resolved", () => {
    expect(
      pendingRequestKind([
        message("turn-started"),
        message("approval-requested", "a1"),
        message("approval-resolved", "a1"),
      ]),
    ).toBe(null);
  });

  it("drops stale requests when the turn ends or a new one starts", () => {
    const stale = [message("turn-started"), message("approval-requested", "a1")];
    expect(pendingRequestKind([...stale, message("turn-completed")])).toBe(null);
    expect(pendingRequestKind([...stale, message("turn-started")])).toBe(null);
  });

  it("exposes the newest unresolved request id for keyboard decisions", () => {
    expect(
      pendingRequest([
        message("turn-started"),
        message("approval-requested", "a1"),
        message("approval-requested", "a2"),
        message("approval-resolved", "a2"),
      ]),
    ).toEqual({ kind: "approval", requestId: "a1" });
  });
});

describe("formatElapsed", () => {
  it("renders seconds, minutes, and hours compactly", () => {
    expect(formatElapsed(0)).toBe("0s");
    expect(formatElapsed(8_400)).toBe("8s");
    expect(formatElapsed(65_000)).toBe("1m 05s");
    expect(formatElapsed(3_720_000)).toBe("1h 02m");
  });

  it("clamps negative input to zero", () => {
    expect(formatElapsed(-5_000)).toBe("0s");
  });
});
