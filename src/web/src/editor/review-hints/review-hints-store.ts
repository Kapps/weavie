import type * as monaco from "monaco-editor";
import { createEffect, createRoot, createSignal } from "solid-js";
import type { ClientSession } from "../../bridge";
import { createSessionFeatureValue } from "../../messaging/session-feature-value";
import { createSessionOwnedResource } from "../../messaging/session-owned-state";

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
}

export function showReviewHints(): void {
  setVisible(true);
}

/**
 * The session's hint whose popover is open. `owner` is the editor showing it; null until a displayed binding of
 * its file claims it (navigation). A pinned popover stays open when the pointer leaves.
 */
export interface OpenHint {
  id: number;
  owner: monaco.editor.ICodeEditor | null;
  pinned: boolean;
}

// One popover per session, closed with its hint: dismissed, replaced by a new review, or hidden.
const opened = createSessionOwnedResource(
  (session) =>
    createRoot((dispose) => {
      const [read, write] = createSignal<OpenHint | null>(null);
      createEffect(() => {
        const open = read();
        if (open === null) return;
        if (!visible() || !reviewHintsFor(session)?.some((hint) => hint.id === open.id))
          write(null);
      });
      return { read, write, dispose };
    }),
  (_session, state) => state.dispose(),
);

export const openedHint = (session: ClientSession): OpenHint | null =>
  opened.get(session)?.read() ?? null;

export function openHint(session: ClientSession, value: OpenHint | null): void {
  opened.get(session)?.write(value);
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
