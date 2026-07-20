// Lets commands focus the single omnibar without prop-threading through TitleBar: a signal the Omnibar
// watches to open + focus itself in the requested mode.

import { createSignal } from "solid-js";

export type OmnibarMode = "file" | "command" | "docSymbol" | "wsSymbol";

const [request, setRequest] = createSignal<{
  mode: OmnibarMode;
  query: string;
  nonce: number;
} | null>(null);

/** The latest focus request (nonce bumps each call so repeats still trigger). */
export const omnibarRequest = request;

let nonce = 0;

/**
 * Asks the omnibar to open + focus in the given mode (file quick-open, command palette, or symbol search).
 * `query` preloads the input after the mode prefix, selected so typing replaces it — "" for a plain open.
 */
export function focusOmnibar(mode: OmnibarMode, query: string): void {
  nonce += 1;
  setRequest({ mode, query, nonce });
}
