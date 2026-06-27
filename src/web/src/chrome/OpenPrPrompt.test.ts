import { describe, expect, it } from "vitest";
import { parsePrRef } from "./pr-ref";

describe("parsePrRef", () => {
  it("parses #N and a bare number to the origin repo", () => {
    expect(parsePrRef("#46")).toEqual({ number: 46, owner: "", repo: "" });
    expect(parsePrRef("  46 ")).toEqual({ number: 46, owner: "", repo: "" });
  });

  it("parses a PR URL, carrying its owner/repo", () => {
    expect(parsePrRef("https://github.com/acme/demo/pull/123")).toEqual({
      number: 123,
      owner: "acme",
      repo: "demo",
    });
    expect(parsePrRef("github.com/acme/demo/pull/7")).toEqual({
      number: 7,
      owner: "acme",
      repo: "demo",
    });
  });

  it("returns null for a free-text search query", () => {
    expect(parsePrRef("fix the login bug")).toBeNull();
    expect(parsePrRef("readme")).toBeNull();
    expect(parsePrRef("")).toBeNull();
  });
});
