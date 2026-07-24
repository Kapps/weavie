import type { SpellIssue, WebBoundMessage } from "../../bridge";

export type SpellDiagnostics = Extract<WebBoundMessage, { type: "spell-diagnostics" }>;
export type SpellSuggestResult = Extract<WebBoundMessage, { type: "spell-suggest-result" }>;
export type SpellAddWordResult = Extract<WebBoundMessage, { type: "spell-add-word-result" }>;
export type SpellDictionaryChanged = Extract<WebBoundMessage, { type: "spell-dictionary-changed" }>;

export type SpellScope = "project" | "user";

/** An active misspelling owned by one Monaco model and retained only while its word span still matches. */
export type SpellContext = SpellIssue & { modelId: string };

export interface SpellMenuTarget {
  context: SpellContext;
  x: number;
  y: number;
}

export type { SpellIssue };
