import type { SpellIssue, WebBoundMessage } from "../../bridge";

export type SpellDiagnostics = Extract<WebBoundMessage, { type: "spell-diagnostics" }>;
export type SpellSuggestResult = Extract<WebBoundMessage, { type: "spell-suggest-result" }>;
export type SpellAddWordResult = Extract<WebBoundMessage, { type: "spell-add-word-result" }>;
export type SpellDictionaryChanged = Extract<WebBoundMessage, { type: "spell-dictionary-changed" }>;

export type SpellScope = "project" | "user";

/** An active misspelling, retained only while its submitted revision and word span still match. */
export type SpellContext = SpellIssue;

export interface SpellMenuTarget {
  context: SpellContext;
  x: number;
  y: number;
}

export type { SpellIssue };
