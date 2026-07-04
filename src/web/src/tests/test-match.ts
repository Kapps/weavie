// The pure test-symbol matcher: no monaco imports, so it unit-tests in isolation. Given a symbol tree, a rule,
// and a header-slice function, it emits a runnable hit per matching symbol with its ancestor-composed name.

import type { TestRule } from "./test-profile";

/** A 1-based range (matches monaco's IRange). */
export interface SymbolRange {
  startLineNumber: number;
  startColumn: number;
  endLineNumber: number;
  endColumn: number;
}

/** The subset of a document symbol the matcher needs. */
export interface SymbolNode {
  name: string;
  range: SymbolRange;
  selectionRange: SymbolRange;
  children?: SymbolNode[];
}

/** A runnable test: its composed `name`, the `range` a lens anchors to (the name), and the `fullRange` of the block. */
export interface TestHit {
  name: string;
  range: SymbolRange;
  fullRange: SymbolRange;
}

/**
 * Walks the symbol tree, emitting a hit for each symbol whose name matches the rule's `symbol` regex (and, when
 * set, whose header slice matches `header`). The test name joins the first capture (or whole name) of every
 * matching ancestor with the rule's separator. `sliceHeader` returns the source between a symbol's range start
 * and its name — where attributes/annotations/decorators live.
 */
export function collectTests(
  symbols: SymbolNode[],
  rule: TestRule,
  sliceHeader: (node: SymbolNode) => string,
): TestHit[] {
  const symbolRe = new RegExp(rule.symbol);
  const headerRe = rule.header !== undefined ? new RegExp(rule.header) : undefined;
  const hits: TestHit[] = [];

  const walk = (nodes: SymbolNode[], prefix: string[]): void => {
    for (const node of nodes) {
      const match = symbolRe.exec(node.name);
      const isTest = match !== null && (headerRe === undefined || headerRe.test(sliceHeader(node)));
      const nextPrefix = isTest ? [...prefix, match?.[1] ?? node.name] : prefix;
      if (isTest) {
        hits.push({
          name: nextPrefix.join(rule.nameSeparator),
          range: node.selectionRange,
          fullRange: node.range,
        });
      }
      if (node.children !== undefined && node.children.length > 0) {
        walk(node.children, nextPrefix);
      }
    }
  };

  walk(symbols, []);
  return hits;
}
