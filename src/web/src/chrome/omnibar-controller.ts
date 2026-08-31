// Lets commands focus the single omnibar without prop-threading through TitleBar: a signal the Omnibar
// watches to open + focus itself in the requested mode.

import { createSignal } from "solid-js";
import { pathSeed } from "./path-query";

export type OmnibarMode = "file" | "command" | "docSymbol" | "wsSymbol";

const [request, setRequest] = createSignal<{
  mode: OmnibarMode;
  query: string;
  line: number;
  /** Whether the preloaded query is selected (replace-on-type) or left with the caret at its end. */
  select: boolean;
  nonce: number;
} | null>(null);

/** The latest focus request (nonce bumps each call so repeats still trigger). */
export const omnibarRequest = request;

let nonce = 0;

/** Asks the omnibar to open + focus in the given mode (file quick-open, command palette, or symbol search). */
export function focusOmnibar(mode: OmnibarMode): void {
  nonce += 1;
  setRequest({ mode, query: "", line: 1, select: true, nonce });
}

/**
 * Host-driven Go-to-File open for resolving an ambiguous file link: `query` preloads the input, selected so
 * typing replaces it, and `line` (the link's 1-based line) applies to whichever file this omnibar session opens.
 */
export function focusOmnibarFileSearch(query: string, line: number): void {
  nonce += 1;
  setRequest({ mode: "file", query, line, select: true, nonce });
}

/**
 * Opens the omnibar seeded with `root` plus its separator, which the path-shape check reads as path mode. The
 * seed is a starting point to type from, not text to replace, so it is left unselected with the caret at the
 * end — one Backspace walks to the parent, which is the sibling-repo case.
 */
export function focusOmnibarPath(root: string): void {
  nonce += 1;
  setRequest({ mode: "file", query: pathSeed(root), line: 1, select: false, nonce });
}
