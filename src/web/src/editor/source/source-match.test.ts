import { describe, expect, it } from "vitest";
import { matchSource } from "./source-match";

// The registry the host pushes for Notion (one declaration, mirrored from NotionSource.NotionHosts in Core).
const NOTION = [{ id: "notion", hosts: ["notion.so", "*.notion.so", "*.notion.site"] }];

describe("matchSource", () => {
  it("claims notion.so hosts (incl. www and workspace subdomains)", () => {
    expect(
      matchSource("https://www.notion.so/hightouch/Doc-38bab9c473d581e5aa47ccf84581a15b", NOTION),
    ).toBe("notion");
    expect(matchSource("https://notion.so/Page-abc", NOTION)).toBe("notion");
    expect(matchSource("https://acme.notion.site/Public-Page", NOTION)).toBe("notion");
  });

  it("returns null for non-source URLs, look-alikes, and junk", () => {
    expect(matchSource("https://example.com/notion.so", NOTION)).toBeNull();
    expect(matchSource("https://evil-notion.so.attacker.com/x", NOTION)).toBeNull();
    expect(matchSource("not a url", NOTION)).toBeNull();
  });

  it("returns null when the registry is empty (before the host pushes it)", () => {
    expect(matchSource("https://www.notion.so/x", [])).toBeNull();
  });
});
