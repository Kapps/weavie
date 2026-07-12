import { describe, expect, it } from "vitest";
import type { AgentPaneUpdate } from "../bridge";
import { activeTurnStartedAt, formatElapsed, hasActiveTurn, pendingRequest } from "./turn-progress";

const message = (type: string, itemId?: string): AgentPaneUpdate => ({
  type,
  providerId: "codex",
  itemId: itemId ?? null,
});

const started = (receivedAt: number): AgentPaneUpdate => ({
  type: "turn-started",
  providerId: "codex",
  receivedAt,
});

describe("hasActiveTurn", () => {
  it("is false with no turn messages", () => {
    expect(hasActiveTurn([message("user-message")])).toBe(false);
  });

  it("is true after a start with no completion", () => {
    expect(hasActiveTurn([message("turn-started")])).toBe(true);
  });

  it("is false again after the turn completes (interruption also arrives as turn-completed)", () => {
    expect(hasActiveTurn([message("turn-started"), message("turn-completed")])).toBe(false);
  });

  it("tracks the latest turn across several", () => {
    expect(
      hasActiveTurn([message("turn-started"), message("turn-completed"), message("turn-started")]),
    ).toBe(true);
  });

  it("ignores subagent lifecycle while the primary turn is running", () => {
    expect(
      hasActiveTurn([
        { ...started(1000), isPrimaryThread: true },
        { ...message("turn-started"), isPrimaryThread: false },
        { ...message("turn-completed"), isPrimaryThread: false },
      ]),
    ).toBe(true);
  });
});

const kindOf = (messages: AgentPaneUpdate[]) => pendingRequest(messages)?.kind ?? null;

describe("pendingRequest", () => {
  it("is null with no unresolved requests", () => {
    expect(kindOf([message("turn-started")])).toBe(null);
  });

  it("reports the latest unresolved request kind", () => {
    expect(
      kindOf([
        message("turn-started"),
        message("approval-requested", "a1"),
        message("input-requested", "q1"),
      ]),
    ).toBe("input");
  });

  it("clears a request once resolved", () => {
    expect(
      kindOf([
        message("turn-started"),
        message("approval-requested", "a1"),
        message("approval-resolved", "a1"),
      ]),
    ).toBe(null);
  });

  it("drops stale requests when the turn ends or a new one starts", () => {
    const stale = [message("turn-started"), message("approval-requested", "a1")];
    expect(kindOf([...stale, message("turn-completed")])).toBe(null);
    expect(kindOf([...stale, message("turn-started")])).toBe(null);
  });

  it("does not clear pending requests at a subagent boundary", () => {
    expect(
      pendingRequest([
        { ...message("turn-started"), isPrimaryThread: true },
        message("approval-requested", "a1"),
        { ...message("turn-completed"), isPrimaryThread: false },
      ]),
    ).toEqual({ kind: "approval", requestId: "a1" });
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

  it("resolves only the request from the matching thread and turn", () => {
    expect(
      pendingRequest([
        message("turn-started"),
        { ...message("approval-requested", "same"), threadId: "root", turnId: "turn" },
        { ...message("input-requested", "same"), threadId: "sub", turnId: "turn" },
        { ...message("input-resolved", "same"), threadId: "sub", turnId: "turn" },
      ]),
    ).toEqual({ kind: "approval", requestId: "same" });
  });

  it("clears a thread-scoped request when a restart cancels it", () => {
    expect(
      pendingRequest([
        message("turn-started"),
        { ...message("approval-requested", "a1"), threadId: "root", turnId: "turn-1" },
        {
          ...message("approval-resolved", "a1"),
          threadId: "root",
          turnId: "turn-1",
          status: "cancel",
        },
      ]),
    ).toBeNull();
  });
});

describe("activeTurnStartedAt", () => {
  it("is null with no active turn", () => {
    expect(activeTurnStartedAt([message("user-message")])).toBe(null);
    expect(activeTurnStartedAt([started(1000), message("turn-completed")])).toBe(null);
  });

  it("returns the running turn's arrival time", () => {
    expect(activeTurnStartedAt([started(1234)])).toBe(1234);
  });

  it("anchors to the latest turn, not an earlier finished one", () => {
    expect(activeTurnStartedAt([started(1000), message("turn-completed"), started(5000)])).toBe(
      5000,
    );
  });

  it("is stable regardless of later activity within the turn", () => {
    expect(
      activeTurnStartedAt([started(2000), message("item-started"), message("item-completed")]),
    ).toBe(2000);
  });

  it("keeps the primary timer across subagent turns", () => {
    expect(
      activeTurnStartedAt([
        { ...started(2000), isPrimaryThread: true },
        { ...started(5000), isPrimaryThread: false },
        { ...message("turn-completed"), isPrimaryThread: false },
      ]),
    ).toBe(2000);
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
