import { describe, expect, it } from "vitest";
import { sourceIdForUrl } from "./source-match";

describe("sourceIdForUrl", () => {
  it("claims notion.so hosts (incl. www and workspace subdomains)", () => {
    expect(
      sourceIdForUrl("https://www.notion.so/hightouch/Doc-38bab9c473d581e5aa47ccf84581a15b"),
    ).toBe("notion");
    expect(sourceIdForUrl("https://notion.so/Page-abc")).toBe("notion");
    expect(sourceIdForUrl("https://acme.notion.site/Public-Page")).toBe("notion");
  });

  it("returns null for non-source URLs and junk", () => {
    expect(sourceIdForUrl("https://example.com/notion.so")).toBeNull();
    expect(sourceIdForUrl("https://evil-notion.so.attacker.com/x")).toBeNull();
    expect(sourceIdForUrl("not a url")).toBeNull();
  });
});
