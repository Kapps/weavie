import { describe, expect, it } from "vitest";
import { externalNavigationTarget } from "./navigation-guard";

const PAGE = "http://127.0.0.1:7420/index.html?ws=abc";

describe("externalNavigationTarget", () => {
  it("routes an external web link", () => {
    expect(externalNavigationTarget("https://example.com/docs", PAGE)).toBe(
      "https://example.com/docs",
    );
  });

  it("routes a same-origin link to another path (it would replace the app)", () => {
    expect(externalNavigationTarget("http://127.0.0.1:7420/other", PAGE)).toBe(
      "http://127.0.0.1:7420/other",
    );
  });

  it("routes a same-path link with different query (a reload would drop the app)", () => {
    expect(externalNavigationTarget("http://127.0.0.1:7420/index.html?ws=zzz", PAGE)).toBe(
      "http://127.0.0.1:7420/index.html?ws=zzz",
    );
  });

  it("allows an in-page #hash on the current page", () => {
    expect(
      externalNavigationTarget("http://127.0.0.1:7420/index.html?ws=abc#sec", PAGE),
    ).toBeNull();
  });

  it("routes a #hash on a different page", () => {
    expect(externalNavigationTarget("https://example.com/doc#sec", PAGE)).toBe(
      "https://example.com/doc#sec",
    );
  });

  it("allows non-web schemes", () => {
    expect(externalNavigationTarget("command:editor.action.foo", PAGE)).toBeNull();
    expect(externalNavigationTarget("mailto:a@b.c", PAGE)).toBeNull();
    expect(externalNavigationTarget("file:///home/u/a.ts", PAGE)).toBeNull();
  });

  it("allows an unparsable href (an anchor with no real target)", () => {
    expect(externalNavigationTarget("", PAGE)).toBeNull();
  });
});
