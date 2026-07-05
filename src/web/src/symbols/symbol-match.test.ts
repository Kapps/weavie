import { describe, expect, it } from "vitest";
import { type DocSymbolNode, flattenDocumentSymbols, rankSymbols } from "./symbol-match";

const R = { startLineNumber: 1, startColumn: 1, endLineNumber: 1, endColumn: 1 };

function node(name: string, kind: string, children?: DocSymbolNode[]): DocSymbolNode {
  return children === undefined
    ? { name, kind, selectionRange: R }
    : { name, kind, selectionRange: R, children };
}

describe("flattenDocumentSymbols", () => {
  it("emits rows in document order with dotted ancestor containers", () => {
    const tree = [
      node("Session", "class", [
        node("open", "method"),
        node("Inner", "class", [node("run", "method")]),
      ]),
      node("helper", "function"),
    ];
    const flat = flattenDocumentSymbols(tree, "/repo/a.ts");
    expect(flat.map((s) => `${s.container}|${s.name}`)).toEqual([
      "|Session",
      "Session|open",
      "Session|Inner",
      "Session.Inner|run",
      "|helper",
    ]);
  });

  it("carries kind, path, and the selectionRange as the nav target", () => {
    const range = { startLineNumber: 12, startColumn: 3, endLineNumber: 12, endColumn: 8 };
    const flat = flattenDocumentSymbols(
      [{ name: "foo", kind: "function", selectionRange: range }],
      "/x.ts",
    );
    expect(flat[0]).toMatchObject({ name: "foo", kind: "function", path: "/x.ts", range });
  });
});

describe("rankSymbols", () => {
  const syms = flattenDocumentSymbols(
    [node("readAll", "method"), node("readFile", "method"), node("write", "method")],
    "/x.ts",
  );

  it("returns input order unranked for an empty query", () => {
    expect(rankSymbols(syms, "  ").map((r) => r.sym.name)).toEqual([
      "readAll",
      "readFile",
      "write",
    ]);
  });

  it("fuzzy-ranks by name and yields highlight positions", () => {
    const ranked = rankSymbols(syms, "read");
    expect(ranked.map((r) => r.sym.name)).toEqual(["readAll", "readFile"]);
    expect(ranked[0]?.positions).toBeInstanceOf(Set);
    expect([...(ranked[0]?.positions ?? [])].sort((a, b) => a - b)).toEqual([0, 1, 2, 3]);
  });

  it("drops non-matches", () => {
    expect(rankSymbols(syms, "zzz")).toEqual([]);
  });
});
