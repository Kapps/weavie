import { describe, expect, it } from "vitest";
import { type SymbolNode, collectTests } from "./test-match";
import type { TestRule } from "./test-profile";

const RANGE = { startLineNumber: 1, startColumn: 1, endLineNumber: 1, endColumn: 1 };

function node(name: string, children?: SymbolNode[]): SymbolNode {
  return children === undefined
    ? { name, range: RANGE, selectionRange: RANGE }
    : { name, range: RANGE, selectionRange: RANGE, children };
}

function rule(over: Partial<TestRule>): TestRule {
  return { glob: "*", symbol: "x", runOne: "o", runFile: "f", nameSeparator: " ", ...over };
}

const noHeader = (): string => "";

describe("collectTests", () => {
  it("composes nested describe/it names (tsserver shape)", () => {
    // The shape captured from tsserver's navtree: describe/it/test call expressions as named nested symbols.
    const symbols = [
      node("describe('math') callback", [
        node("it('adds') callback"),
        node("test('subtracts') callback"),
        node("describe('nested') callback", [node("it('multiplies') callback")]),
      ]),
    ];
    const r = rule({
      symbol: "^(?:describe|it|test)\\((?:'|\")(.+?)(?:'|\")",
      nameSeparator: " > ",
    });
    const names = collectTests(symbols, r, noHeader).map((h) => h.name);
    expect(names).toEqual([
      "math",
      "math > adds",
      "math > subtracts",
      "math > nested",
      "math > nested > multiplies",
    ]);
  });

  it("selects TestXxx functions and ignores helpers (gopls shape)", () => {
    const symbols = [node("TestAdds"), node("TestSubtracts"), node("helper")];
    const names = collectTests(symbols, rule({ symbol: "^(Test\\w+)" }), noHeader).map(
      (h) => h.name,
    );
    expect(names).toEqual(["TestAdds", "TestSubtracts"]);
  });

  it("uses the header slice to select attribute-based tests (csharp-ls shape)", () => {
    // csharp-ls includes attribute lines in a method symbol's range; the header slice is that region.
    const headers: Record<string, string> = {
      "Adds()": "\t[Fact]\n\tpublic void ",
      "Subtracts(int x)": "\n\t[Theory]\n\t[InlineData(1)]\n\tpublic void ",
      "NotATest()": "\n\tpublic void ",
    };
    const symbols = [node("Adds()"), node("Subtracts(int x)"), node("NotATest()")];
    const r = rule({ symbol: "^(\\w+)\\(", header: "\\[(Fact|Theory)\\b" });
    const names = collectTests(symbols, r, (n) => headers[n.name] ?? "").map((h) => h.name);
    expect(names).toEqual(["Adds", "Subtracts"]);
  });

  it("falls back to the whole symbol name when the regex has no capture group", () => {
    const names = collectTests([node("TestAdds")], rule({ symbol: "^Test\\w+" }), noHeader).map(
      (h) => h.name,
    );
    expect(names).toEqual(["TestAdds"]);
  });
});
