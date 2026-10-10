import type * as monaco from "monaco-editor";
import { createSignal } from "solid-js";
import type { ClientSession } from "../../bridge";
import { setContext } from "../../commands/context";
import { createSessionFeatureValue } from "../../messaging/session-feature-value";

export type HintSeverity = "bug" | "warn" | "nit";

/** One agent finding, numbered against the file text it was captured from. */
export interface ReviewHint {
  id: number;
  path: string;
  line: number;
  severity: HintSeverity;
  title: string;
  body: string;
  category: string | null;
  agent: string;
  sourceHash: string;
  quote: string;
}

export const reviewHintsFor: (session: ClientSession | null) => ReviewHint[] | null =
  createSessionFeatureValue<{ hints: ReviewHint[] }, ReviewHint[]>(
    "reviewHints",
    "hints",
    (message) => message.hints,
  );

const [visible, setVisible] = createSignal(true);

/** Whether review hints are shown; one switch for every editor. */
export const reviewHintsVisible = visible;

export function toggleReviewHints(): void {
  setVisible((shown) => !shown);
  if (!visible()) openHint(null);
}

export function showReviewHints(): void {
  setVisible(true);
}

/**
 * The hint whose popover is open. `owner` is the editor showing it; null until a displayed binding of its file
 * claims it (navigation). A pinned popover stays open when the pointer leaves.
 */
export interface OpenHint {
  session: ClientSession;
  id: number;
  owner: monaco.editor.ICodeEditor | null;
  pinned: boolean;
}

const [opened, setOpened] = createSignal<OpenHint | null>(null);

export const openedHint = opened;

export function openHint(value: OpenHint | null): void {
  setOpened(value);
  setContext("reviewHintOpen", value !== null);
}

export function dismissHint(session: ClientSession, id: number): Promise<void> {
  return session.feature("reviewHints").request<void, { id: number }>("dismiss", { id });
}

export function fetchHintSource(
  session: ClientSession,
  path: string,
  hash: string,
): Promise<string> {
  return session
    .feature("reviewHints")
    .request<{ text: string }, { path: string; hash: string }>("source", { path, hash })
    .then((source) => source.text);
}

/** The text sent to the agent for a hint: its summary, then its details. */
export function hintText(hint: ReviewHint): string {
  return hint.body === "" ? hint.title : `${hint.title}\n\n${hint.body}`;
}
