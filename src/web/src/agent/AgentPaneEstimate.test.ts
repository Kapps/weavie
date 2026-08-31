import { describe, expect, it } from "vitest";
import { estimateEntrySize } from "./AgentPaneEstimate";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";

// The estimate decides how far the pane's geometry moves when a row is finally measured, so what
// matters is that it models how each kind of content actually renders: prose wraps at the markdown
// measure, code and tool output do not wrap at all and sit on a smaller leading.
const PROSE_LINE = 27;
const MONO_LINE = 18;
const PROSE_CHROME = 12;
const OTHER_CHROME = 34;

function entry(over: Partial<AgentTranscriptEntry>): AgentTranscriptEntry {
  return {
    actionMessage: null,
    detailCount: 0,
    details: [],
    id: "e",
    kind: "message",
    label: "",
    status: null,
    streaming: false,
    summary: null,
    text: null,
    tone: "assistant",
    ...over,
  };
}

describe("estimateEntrySize", () => {
  it("wraps assistant prose at the markdown measure, not at 48 columns", () => {
    expect(estimateEntrySize(entry({ text: "x".repeat(96) }))).toBe(PROSE_CHROME + PROSE_LINE);
    expect(estimateEntrySize(entry({ text: "y".repeat(97) }))).toBe(PROSE_CHROME + PROSE_LINE * 2);
  });

  it("counts a fenced block as one unwrapped line per source line", () => {
    const code = ["```ts", "a();", "b();", "c();", "```"].join("\n");
    expect(estimateEntrySize(entry({ text: code }))).toBe(PROSE_CHROME + MONO_LINE * 3);
  });

  it("does not treat short code lines as wrapped prose", () => {
    const long = entry({ text: ["```ts", ...Array(40).fill("a();"), "```"].join("\n") });
    // 40 short lines are 40 rendered lines, where a character-count model would predict about four.
    expect(estimateEntrySize(long)).toBe(PROSE_CHROME + MONO_LINE * 40);
  });

  it("treats non-assistant text as preformatted, one line per source line", () => {
    const output = entry({ kind: "activity", tone: "activity", text: "a\nb\nc" });
    expect(estimateEntrySize(output)).toBe(OTHER_CHROME + MONO_LINE * 3);
  });

  it("leaves room for a collapsed history toggle", () => {
    const plain = entry({ kind: "activity", tone: "activity", summary: "ran a command" });
    const withDetails = entry({
      detailCount: 4,
      kind: "activity",
      summary: "ran a command",
      tone: "activity",
    });
    expect(estimateEntrySize(withDetails) - estimateEntrySize(plain)).toBe(MONO_LINE);
  });

  it("memoizes per entry, since it runs for every unmeasured row on each pass", () => {
    const shared = entry({ text: "hello" });
    expect(estimateEntrySize(shared)).toBe(estimateEntrySize(shared));
  });
});
