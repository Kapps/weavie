// Lets commands focus the single omnibar without prop-threading through TitleBar: a signal the Omnibar
// watches to open + focus itself in the requested mode.

import { createSignal } from "solid-js";

export type OmnibarMode = "file" | "command" | "docSymbol" | "wsSymbol";

const [request, setRequest] = createSignal<{
  mode: OmnibarMode;
  query: string;
  line: number;
  nonce: number;
} | null>(null);

/** The latest focus request (nonce bumps each call so repeats still trigger). */
export const omnibarRequest = request;

let nonce = 0;

/** Asks the omnibar to open + focus in the given mode (file quick-open, command palette, or symbol search). */
export function focusOmnibar(mode: OmnibarMode): void {
  nonce += 1;
  setRequest({ mode, query: "", line: 1, nonce });
}

/**
 * Host-driven Go-to-File open for resolving an ambiguous file link: `query` preloads the input, selected so
 * typing replaces it, and `line` (the link's 1-based line) applies to whichever file this omnibar session opens.
 */
export function focusOmnibarFileSearch(query: string, line: number): void {
  nonce += 1;
  setRequest({ mode: "file", query, line, nonce });
}
