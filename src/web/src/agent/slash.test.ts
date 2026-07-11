import { describe, expect, it } from "vitest";
import type { AgentSlashEntry } from "../bridge";
import { filterSlash, slashQuery } from "./slash";

const entry = (name: string): AgentSlashEntry => ({
  id: name,
  name,
  description: name,
  commandId: null,
  insertText: name,
  skillName: null,
});

describe("slashQuery", () => {
  it("returns the query while the draft is a whitespace-free slash token", () => {
    expect(slashQuery("/")).toBe("");
    expect(slashQuery("/mod")).toBe("mod");
  });

  it("is inactive once the draft is a prompt or not a slash command", () => {
    expect(slashQuery("")).toBeNull();
    expect(slashQuery("hello")).toBeNull();
    expect(slashQuery("/model do the thing")).toBeNull();
    expect(slashQuery(" /model")).toBeNull();
  });
});

describe("filterSlash", () => {
  const entries = [entry("model"), entry("approvals"), entry("sandbox"), entry("review-pr")];

  it("returns all entries for an empty query", () => {
    expect(filterSlash(entries, "").map((match) => match.name)).toEqual([
      "model",
      "approvals",
      "sandbox",
      "review-pr",
    ]);
  });

  it("filters by case-insensitive substring", () => {
    expect(filterSlash(entries, "AP").map((match) => match.name)).toEqual(["approvals"]);
    expect(filterSlash(entries, "an").map((match) => match.name)).toEqual(["sandbox"]);
    expect(filterSlash(entries, "review").map((match) => match.name)).toEqual(["review-pr"]);
  });

  it("caps the list at eight entries", () => {
    const many = Array.from({ length: 20 }, (_, index) => entry(`skill-${index}`));
    expect(filterSlash(many, "skill")).toHaveLength(8);
  });
});
