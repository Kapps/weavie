import { describe, expect, it } from "vitest";
import { type CommandCandidate, type CommandUsage, rankCommands } from "./ranking";

const candidate = (id: string): CommandCandidate<string> => ({
  item: id,
  id,
  title: id,
  aliases: [],
  metadata: "",
});
const signal = (id: string, query: string, weight: number): CommandUsage => ({
  id,
  query,
  weight,
  lastUsed: 0,
});
const rank = (query: string, usage: CommandUsage[]) =>
  rankCommands(
    [candidate("Review"), candidate("Reload"), candidate("Reopen terminal")],
    query,
    usage,
  ).map((row) => row.item);

describe("command ranking", () => {
  it("promotes recent/frequent commands on empty input and never adds unavailable commands", () => {
    expect(rank("", [signal("Review", "", 2), signal("Unavailable", "", 100)])).toEqual([
      "Review",
      "Reload",
      "Reopen terminal",
    ]);
  });
  it("learns an abbreviation without overriding exact intent", () => {
    const usage = [signal("Reopen terminal", "re", 3), signal("Reopen terminal", "review", 999)];
    expect(rank(" RE ", usage)[0]).toBe("Reopen terminal");
    expect(rank("Review", usage)[0]).toBe("Review");
    expect(rank("rel", usage)).toEqual(["Reload", "Reopen terminal"]);
  });
  it("lets stale query learning fade back behind stronger text matches", () => {
    const candidates = [candidate("Run tests"), candidate("Read output")];
    expect(rankCommands(candidates, "rt", [signal("Read output", "rt", 2)])[0]?.item).toBe(
      "Read output",
    );
    expect(rankCommands(candidates, "rt", [signal("Read output", "rt", 0.001)])[0]?.item).toBe(
      "Run tests",
    );
  });
  it("preserves name relevance ahead of heavily used metadata matches and accepts aliases", () => {
    const candidates = [
      candidate("Review"),
      { ...candidate("Audit"), metadata: "Review", aliases: ["inspect"] },
    ];
    expect(rankCommands(candidates, "rev", [signal("Audit", "rev", 100)])[0]?.item).toBe("Review");
    expect(rankCommands(candidates, "inspect", [])[0]?.item).toBe("Audit");
  });
  it("matches description terms without treating scattered prose letters as a command name", () => {
    const candidates = [
      candidate("review-pr"),
      { ...candidate("clear"), metadata: "Clear the transcript and start a fresh conversation" },
    ];
    expect(rankCommands(candidates, "rev", []).map((row) => row.item)).toEqual(["review-pr"]);
    expect(rankCommands(candidates, "fresh conversation", []).map((row) => row.item)).toEqual([
      "clear",
    ]);
  });
  it("keeps title highlight positions and deterministic ties", () => {
    const rows = rankCommands([candidate("Reload"), candidate("Review")], "re", []);
    expect(rows.map((row) => row.item)).toEqual(["Reload", "Review"]);
    expect(rows[0]!.positions).toEqual(new Set([0, 1]));
  });
});
