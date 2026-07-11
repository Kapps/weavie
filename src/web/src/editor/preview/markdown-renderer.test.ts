import { describe, expect, it } from "vitest";
import { isSafeAgentLink } from "./markdown-renderer";

describe("agent Markdown link policy", () => {
  it("allows web and workspace targets", () => {
    expect(isSafeAgentLink("https://example.com/path")).toBe(true);
    expect(isSafeAgentLink("src/app.ts:12")).toBe(true);
    expect(isSafeAgentLink("C:\\repo\\app.cs:7")).toBe(true);
  });

  it("rejects executable, embedded-data, and direct file schemes", () => {
    expect(isSafeAgentLink("javascript:alert(1)")).toBe(false);
    expect(isSafeAgentLink("data:text/html,hello")).toBe(false);
    expect(isSafeAgentLink("file:///etc/passwd")).toBe(false);
    expect(isSafeAgentLink("vscode://file/repo/app.ts")).toBe(false);
  });
});
