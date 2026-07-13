import { describe, expect, it } from "vitest";
import type { AgentPaneUpdate } from "../bridge";
import { requestLifecycles } from "./AgentPaneMessageFormat";

const msg = (fields: Partial<AgentPaneUpdate> & { type: string }): AgentPaneUpdate => ({
  providerId: "codex",
  ...fields,
});

describe("requestLifecycles", () => {
  it("reports an open request with its kind and answerable id", () => {
    expect(requestLifecycles([msg({ type: "approval-requested", itemId: "a1" })])).toEqual([
      {
        key: JSON.stringify([null, null, "a1"]),
        requestId: "a1",
        kind: "approval",
        resolvedStatus: null,
      },
    ]);
  });

  it("marks a request resolved with the normalized decision status", () => {
    const [record] = requestLifecycles([
      msg({ type: "approval-requested", itemId: "a1" }),
      msg({ type: "approval-resolved", itemId: "a1", status: "acceptForSession" }),
    ]);
    expect(record?.resolvedStatus).toBe("accepted for session");
  });

  it("prefers a decision status over a bare resolved mirror, in either order", () => {
    const decisionFirst = requestLifecycles([
      msg({ type: "approval-requested", itemId: "a1" }),
      msg({ type: "approval-resolved", itemId: "a1", status: "accept" }),
      msg({ type: "approval-resolved", itemId: "a1", status: "resolved" }),
    ]);
    const mirrorFirst = requestLifecycles([
      msg({ type: "approval-requested", itemId: "a1" }),
      msg({ type: "approval-resolved", itemId: "a1", status: "resolved" }),
      msg({ type: "approval-resolved", itemId: "a1", status: "accept" }),
    ]);
    expect(decisionFirst[0]?.resolvedStatus).toBe("accepted");
    expect(mirrorFirst[0]?.resolvedStatus).toBe("accepted");
  });

  it("scopes a resolution to its own thread when itemIds collide across threads", () => {
    const records = requestLifecycles([
      msg({ type: "approval-requested", threadId: "root", itemId: "same" }),
      msg({ type: "approval-requested", threadId: "sub", itemId: "same" }),
      msg({ type: "approval-resolved", threadId: "sub", itemId: "same", status: "accept" }),
    ]);
    expect(records.map((r) => r.resolvedStatus)).toEqual([null, "accepted"]);
  });

  it("ignores a resolution with no matching request (inert, carries no card)", () => {
    expect(
      requestLifecycles([msg({ type: "approval-resolved", itemId: "ghost", status: "accept" })]),
    ).toEqual([]);
  });

  it("keeps first-requested order across interleaved kinds", () => {
    const records = requestLifecycles([
      msg({ type: "approval-requested", itemId: "a1" }),
      msg({ type: "input-requested", itemId: "q1" }),
    ]);
    expect(records.map((r) => [r.requestId, r.kind])).toEqual([
      ["a1", "approval"],
      ["q1", "input"],
    ]);
  });

  it("does not mistake a prototype property name for a request type", () => {
    expect(requestLifecycles([msg({ type: "toString", itemId: "x1" })])).toEqual([]);
  });
});
