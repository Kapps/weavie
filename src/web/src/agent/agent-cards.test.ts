import { describe, expect, it, vi } from "vitest";

vi.mock("../commands/registry", () => ({ runCommandWithFeedback: vi.fn() }));
vi.mock("../commands/keys-live", () => ({ liveKeyHint: () => "" }));
vi.mock("./agent-command-owner", () => ({ ownsCommand: vi.fn(), ownTarget: vi.fn() }));

import { cardForTab, cardTabPath } from "../editor/tab-entry";
import { namedCard } from "./agent-cards";

describe("card tabs", () => {
  it("round-trips every card through its tab path", () => {
    for (const card of [
      { kind: "subagent", conversationId: "c" },
      { kind: "aside", conversationId: "side" },
      { kind: "dashboard", conversationId: null, dashboardId: "rollout" },
      { kind: "dashboard", conversationId: "side", dashboardId: "rollout" },
      { kind: "block", conversationId: null, blockId: "p95" },
    ] as const) {
      expect(cardForTab(card.kind, cardTabPath(card))).toEqual(card);
    }
    expect(cardTabPath({ kind: "dashboard", conversationId: null, dashboardId: "d" })).toBe(
      "dashboard:d@",
    );
    expect(cardForTab("file", "/a")).toBeNull();
  });
});

describe("named cards", () => {
  it("reads a command's card, names none for no card arguments, and refuses a half-named one", () => {
    expect(namedCard(undefined)).toBeNull();
    expect(namedCard({ background: true })).toBeNull();
    expect(namedCard({ kind: "dashboard", dashboardId: "rollout" })).toEqual({
      kind: "dashboard",
      conversationId: null,
      dashboardId: "rollout",
    });
    expect(namedCard({ kind: "aside", conversationId: "side", collapsed: true })).toEqual({
      kind: "aside",
      conversationId: "side",
    });
    expect(() => namedCard({ dashboardId: "rollout" })).toThrow("Name a card by kind");
    expect(() => namedCard({ kind: "dashboard" })).toThrow("Name a card by kind");
    expect(() => namedCard({ kind: "aside", conversationId: 7 })).toThrow("must be a string");
  });
});
