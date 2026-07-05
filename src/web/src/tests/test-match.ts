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

/**
 * The innermost (smallest) matching test whose block contains `position`, or undefined when the cursor is in
 * none. Containment is by line — the start line counts at any column, since a server may range a test over its
 * callback body (tsgo starts at `() => {`, past the `it(` call and the test name).
 */
export function innermostHitAt(
  hits: TestHit[],
  position: { lineNumber: number; column: number },
): TestHit | undefined {
  return hits
    .filter((h) => containsByLine(h.fullRange, position))
    .sort((a, b) => lineSpan(a.fullRange) - lineSpan(b.fullRange))[0];
}

function containsByLine(
  range: SymbolRange,
  position: { lineNumber: number; column: number },
): boolean {
  if (position.lineNumber < range.startLineNumber || position.lineNumber > range.endLineNumber) {
    return false;
  }
  return position.lineNumber !== range.endLineNumber || position.column <= range.endColumn;
}

function lineSpan(range: SymbolRange): number {
  return range.endLineNumber - range.startLineNumber;
}
