// Lets commands focus the single omnibar without prop-threading through TitleBar: a signal the Omnibar
// watches to open + focus itself in the requested mode.

import { createSignal } from "solid-js";

export type OmnibarMode = "file" | "command";

const [request, setRequest] = createSignal<{ mode: OmnibarMode; nonce: number } | null>(null);

/** The latest focus request (nonce bumps each call so repeats still trigger). */
export const omnibarRequest = request;

let nonce = 0;

/** Asks the omnibar to open + focus in the given mode (file quick-open or command palette). */
export function focusOmnibar(mode: OmnibarMode): void {
  nonce += 1;
  setRequest({ mode, nonce });
}
