import { describe, expect, it } from "vitest";
import type { AgentPaneUpdate } from "../bridge";
import { paneItemIdentity } from "./AgentPaneIdentity";
import {
  activeTurnStartedAt,
  formatElapsed,
  hasActiveTurn,
  hasInterruptibleActivity,
  pendingApproval,
  pendingRequest,
} from "./turn-progress";

const message = (type: string, itemId?: string): AgentPaneUpdate => ({
  type,
  providerId: "acp",
  itemId: itemId ?? null,
  requestId: type.endsWith("-requested") ? (itemId ?? null) : null,
});

const started = (startedAtMs: number): AgentPaneUpdate => ({
  type: "turn-started",
  providerId: "acp",
  startedAtMs,
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

  it("stays active while background work outlives the primary turn", () => {
    const tool = { ...message("item-started", "background"), itemType: "tool", background: true };
    expect(hasActiveTurn([started(1000), tool, message("turn-completed")])).toBe(true);
    expect(
      hasActiveTurn([
        started(1000),
        tool,
        message("turn-completed"),
        { ...message("item-completed", "background"), itemType: "tool", background: true },
      ]),
    ).toBe(false);
  });
});

describe("hasInterruptibleActivity", () => {
  const side = (type: string): AgentPaneUpdate => ({
    type,
    providerId: "acp",
    conversationId: "aside-1",
    isPrimaryThread: false,
  });

  it("tracks a fork before its child turn starts", () => {
    expect(
      hasInterruptibleActivity([{ ...side("side-conversation-started"), status: "forking" }]),
    ).toBe(true);
  });

  it("tracks a text-only child turn without treating it as the primary turn", () => {
    const updates = [
      side("turn-started"),
      { ...side("item-started"), itemId: "tool", itemType: "tool" },
    ];
    expect(hasActiveTurn(updates)).toBe(false);
    expect(hasInterruptibleActivity(updates)).toBe(true);
  });

  it("tracks side authentication and ends when the side becomes terminal", () => {
    const updates = [
      { ...side("side-conversation-started"), status: "forking" },
      { ...side("authentication-requested"), requestId: "aside-1:auth" },
      side("side-conversation-failed"),
    ];
    expect(hasInterruptibleActivity(updates)).toBe(false);
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

  it("keeps an unresolved request across turn boundaries until it is resolved", () => {
    const pending = [message("turn-started"), message("approval-requested", "a1")];
    // A turn boundary must not drop a still-actionable card; only its matching resolution clears it.
    expect(kindOf([...pending, message("turn-completed")])).toBe("approval");
    expect(kindOf([...pending, message("turn-completed"), message("turn-started")])).toBe(
      "approval",
    );
    expect(
      kindOf([...pending, message("turn-completed"), message("approval-resolved", "a1")]),
    ).toBe(null);
  });

  it("does not clear pending requests at a subagent boundary", () => {
    expect(
      pendingRequest([
        { ...message("turn-started"), isPrimaryThread: true },
        message("approval-requested", "a1"),
        { ...message("turn-completed"), isPrimaryThread: false },
      ]),
    ).toEqual({
      key: paneItemIdentity(message("approval-requested", "a1")),
      kind: "approval",
      requestId: "a1",
    });
  });

  it("exposes the newest unresolved request id for keyboard decisions", () => {
    expect(
      pendingRequest([
        message("turn-started"),
        message("approval-requested", "a1"),
        message("approval-requested", "a2"),
        message("approval-resolved", "a2"),
      ]),
    ).toEqual({
      key: paneItemIdentity(message("approval-requested", "a1")),
      kind: "approval",
      requestId: "a1",
    });
  });

  it("resolves only the request from the matching thread and turn", () => {
    expect(
      pendingRequest([
        message("turn-started"),
        { ...message("approval-requested", "same"), threadId: "root", turnId: "turn" },
        { ...message("input-requested", "same"), threadId: "sub", turnId: "turn" },
        { ...message("input-resolved", "same"), threadId: "sub", turnId: "turn" },
      ]),
    ).toEqual({
      key: paneItemIdentity({
        ...message("approval-requested", "same"),
        threadId: "root",
        turnId: "turn",
      }),
      kind: "approval",
      requestId: "same",
    });
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

describe("pendingApproval", () => {
  it("is the newest unresolved approval, regardless of turn state", () => {
    expect(
      pendingApproval([
        message("turn-started"),
        message("approval-requested", "a1"),
        message("turn-completed"),
      ]),
    ).toEqual({
      key: paneItemIdentity(message("approval-requested", "a1")),
      kind: "approval",
      requestId: "a1",
    });
  });

  it("is null when the newest unresolved request is an input, not an approval", () => {
    expect(
      pendingApproval([message("approval-requested", "a1"), message("input-requested", "q1")]),
    ).toBe(null);
  });

  it("clears once the approval resolves", () => {
    expect(
      pendingApproval([message("approval-requested", "a1"), message("approval-resolved", "a1")]),
    ).toBe(null);
  });
});

describe("activeTurnStartedAt", () => {
  it("is null with no active turn", () => {
    expect(activeTurnStartedAt([message("user-message")])).toBe(null);
    expect(activeTurnStartedAt([started(1000), message("turn-completed")])).toBe(null);
  });

  it("returns the running turn's provider timestamp", () => {
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

  it("uses the oldest live background start after the primary turn settles", () => {
    expect(
      activeTurnStartedAt([
        started(1000),
        {
          ...message("item-started", "background"),
          itemType: "tool",
          background: true,
          startedAtMs: 1500,
        },
        message("turn-completed"),
      ]),
    ).toBe(1500);
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
