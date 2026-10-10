// Lets commands focus the single omnibar without prop-threading through TitleBar: a signal the Omnibar
// watches to open + focus itself in the requested mode.

import { createSignal } from "solid-js";
import { pathSeed } from "./path-query";

export type OmnibarMode = "file" | "command" | "docSymbol" | "wsSymbol";

/** A narrowed file set the file modes pick from instead of the workspace, e.g. the open review's files. */
export interface OmnibarFileScope {
  label: string;
  files: () => readonly string[];
  current: () => string | null;
  choose: (path: string) => void;
}

const [request, setRequest] = createSignal<{
  mode: OmnibarMode;
  query: string;
  /** The 1-based line whatever file this session opens should reveal; undefined leaves the tab where it was. */
  line: number | undefined;
  /** Whether the preloaded query is selected (replace-on-type) or left with the caret at its end. */
  select: boolean;
  scope: OmnibarFileScope | null;
  nonce: number;
} | null>(null);

/** The latest focus request (nonce bumps each call so repeats still trigger). */
export const omnibarRequest = request;

let nonce = 0;

/** Asks the omnibar to open + focus in the given mode (file quick-open, command palette, or symbol search). */
export function focusOmnibar(mode: OmnibarMode): void {
  nonce += 1;
  setRequest({ mode, query: "", line: undefined, select: true, scope: null, nonce });
}

/**
 * Host-driven Go-to-File open for resolving an ambiguous file link: `query` preloads the input, selected so
 * typing replaces it, and `line` (the link's 1-based line) applies to whichever file this omnibar session opens.
 */
export function focusOmnibarFileSearch(query: string, line: number | undefined): void {
  nonce += 1;
  setRequest({ mode: "file", query, line, select: true, scope: null, nonce });
}

/**
 * Opens the omnibar seeded with `root` plus its separator, which the path-shape check reads as path mode. The
 * seed is a starting point to type from, not text to replace, so it is left unselected with the caret at the
 * end — one Backspace walks to the parent, which is the sibling-repo case.
 */
export function focusOmnibarPath(root: string): void {
  nonce += 1;
  setRequest({
    mode: "file",
    query: pathSeed(root),
    line: undefined,
    select: false,
    scope: null,
    nonce,
  });
}

/** Opens the file tree and search over `scope`'s files; choosing one hands it to the scope, not the editor. */
export function focusOmnibarScope(scope: OmnibarFileScope): void {
  nonce += 1;
  setRequest({ mode: "file", query: "", line: undefined, select: true, scope, nonce });
}
