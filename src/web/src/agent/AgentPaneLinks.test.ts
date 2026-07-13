import { describe, expect, it } from "vitest";
import { linkAgentText } from "./AgentPaneLinkify";

describe("native agent transcript links", () => {
  it("links web URLs without trailing sentence punctuation", () => {
    expect(linkAgentText("Open https://example.com/pull/328.", false)).toEqual([
      { kind: "text", text: "Open " },
      { kind: "url", text: "https://example.com/pull/328", target: "https://example.com/pull/328" },
      { kind: "text", text: "." },
    ]);
  });

  it("reveals file URIs and honors their line fragment", () => {
    expect(linkAgentText("file:///home/user/a%20b.ts#12", false)).toEqual([
      { kind: "file", text: "file:///home/user/a%20b.ts#12", path: "/home/user/a b.ts", line: 12 },
    ]);
  });

  it("links absolute, relative, and Windows file locations", () => {
    expect(linkAgentText("/home/u/a.png src/web/App.tsx:36 C:\\src\\a.cs:7", false)).toEqual([
      { kind: "file", text: "/home/u/a.png", path: "/home/u/a.png", line: 1 },
      { kind: "text", text: " " },
      { kind: "file", text: "src/web/App.tsx:36", path: "src/web/App.tsx", line: 36 },
      { kind: "text", text: " " },
      { kind: "file", text: "C:\\src\\a.cs:7", path: "C:\\src\\a.cs", line: 7 },
    ]);
  });

  it("only links forge refs when a ref base is available", () => {
    expect(linkAgentText("PR #328", true)[1]).toEqual({ kind: "ref", text: "#328", number: "328" });
    expect(linkAgentText("PR #328", false)).toEqual([{ kind: "text", text: "PR #328" }]);
  });

  it("reveals a bare path whose filename contains @", () => {
    const path = "src/web/e2e/.recordings/page@883bef3dba4a5a81116faeb690fc011f.webm";
    expect(linkAgentText(path, false)).toEqual([{ kind: "file", text: path, path, line: 1 }]);
  });
});
