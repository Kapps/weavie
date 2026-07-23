import type { SpellIssue, WebBoundMessage } from "../../bridge";

export type SpellCheckResult = Extract<WebBoundMessage, { type: "spell-check-result" }>;
export type SpellSuggestResult = Extract<WebBoundMessage, { type: "spell-suggest-result" }>;
export type SpellAddWordResult = Extract<WebBoundMessage, { type: "spell-add-word-result" }>;
export type SpellRestoreResult = Extract<WebBoundMessage, { type: "spell-restore-result" }>;
export type SpellDictionaryChanged = Extract<WebBoundMessage, { type: "spell-dictionary-changed" }>;

export type SpellScope = "project" | "user";

/** One Core-restored manually authored line, using Monaco's 1-based line numbers. */
export interface RestoredSpellLine {
  line: number;
  text: string;
}

/** An active misspelling, retained only while its source line and tracked anchor still match. */
export interface SpellContext {
  anchorId: string;
  word: string;
  startColumn: number;
  endColumn: number;
  modelEpoch: string;
}

export interface SpellMenuTarget {
  context: SpellContext;
  x: number;
  y: number;
}

export type { SpellIssue };
