import { describe, expect, it } from "vitest";
import type { AgentPaneUpdate } from "../bridge";
import {
  caretOnFirstLine,
  caretOnLastLine,
  type HistoryCursor,
  IDLE_CURSOR,
  recallNext,
  recallPrevious,
  submittedPrompts,
} from "./prompt-history";

const user = (text: string): AgentPaneUpdate => ({
  type: "user-message",
  providerId: "codex",
  text,
});
const steer = (text: string): AgentPaneUpdate => ({
  type: "user-steer",
  providerId: "codex",
  text,
});

describe("submittedPrompts", () => {
  it("collects user prompts and steers oldest-first, dropping blanks and adjacent dupes", () => {
    const messages: AgentPaneUpdate[] = [
      user("first"),
      { type: "item-completed", providerId: "codex", itemType: "agentMessage", text: "answer" },
      user("  "),
      steer("second"),
      user("second"),
      user("third"),
    ];
    expect(submittedPrompts(messages)).toEqual(["first", "second", "third"]);
  });

  it("returns an empty list when nothing was submitted", () => {
    expect(submittedPrompts([{ type: "warning", providerId: "codex", text: "x" }])).toEqual([]);
  });
});

describe("recallPrevious / recallNext", () => {
  const history = ["a", "b", "c"];

  it("walks from the newest and stashes the live draft on the first step up", () => {
    const first = recallPrevious(history, IDLE_CURSOR, "draft");
    expect(first).toEqual({ text: "c", next: { cursor: 2, stash: "draft" } });

    const second = recallPrevious(history, first!.next, "ignored");
    expect(second).toEqual({ text: "b", next: { cursor: 1, stash: "draft" } });
  });

  it("clamps at the oldest prompt", () => {
    const state: HistoryCursor = { cursor: 0, stash: "draft" };
    expect(recallPrevious(history, state, "x")).toEqual({
      text: "a",
      next: { cursor: 0, stash: "draft" },
    });
  });

  it("does nothing when there is no history", () => {
    expect(recallPrevious([], IDLE_CURSOR, "draft")).toBeNull();
  });

  it("walks back down and restores the stashed draft past the newest", () => {
    const state: HistoryCursor = { cursor: 1, stash: "draft" };
    const down = recallNext(history, state);
    expect(down).toEqual({ text: "c", next: { cursor: 2, stash: "draft" } });

    const restored = recallNext(history, down!.next);
    expect(restored).toEqual({ text: "draft", next: IDLE_CURSOR });
  });

  it("does nothing when already editing the live draft", () => {
    expect(recallNext(history, IDLE_CURSOR)).toBeNull();
  });
});

describe("caret line detection", () => {
  it("detects the first line", () => {
    expect(caretOnFirstLine("one\ntwo", 2)).toBe(true);
    expect(caretOnFirstLine("one\ntwo", 5)).toBe(false);
  });

  it("detects the last line", () => {
    expect(caretOnLastLine("one\ntwo", 5)).toBe(true);
    expect(caretOnLastLine("one\ntwo", 2)).toBe(false);
  });
});
