import type { ClientSession } from "../../bridge";
import { registerCommand } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import { notify } from "../../notify/notify";
import { editorContexts, type TextEditorConnection } from "../editor-context";
import { type FixItem, fixLine, fixWith } from "../FixPrompt";
import { activeTabFor } from "../session-store";
import { SESSION_FILE_SCHEME, sessionUriHostPath } from "../session-uri-owner";
import type { PlacedHint } from "./hint-anchor";
import { type HintPosition, stepHint } from "./hint-order";
import {
  dismissHint,
  hintText,
  openedHint,
  openHint,
  reviewHintsFor,
  showReviewHints,
  toggleReviewHints,
} from "./review-hints-store";

type RevealFile = (session: ClientSession, path: string, line: number) => void;

const cursorLine = (session: ClientSession): number | undefined =>
  editorContexts.get(session)?.editor.getPosition()?.lineNumber;

// The open hint, else the one on the cursor's line, with the editor binding showing it.
function targetEntry(
  session: ClientSession,
): { connection: TextEditorConnection; placed: PlacedHint } | undefined {
  const open = openedHint(session);
  const connection =
    open !== null && open.owner !== null
      ? editorContexts.fromEditor(open.owner)
      : editorContexts.get(session);
  const line = cursorLine(session);
  const placed = connection?.reviewHints
    .placed()
    .find((entry) =>
      open !== null && open.owner !== null ? entry.hint.id === open.id : entry.line === line,
    );
  return connection === undefined || placed === undefined ? undefined : { connection, placed };
}

const targetHint = (session: ClientSession): PlacedHint | undefined => targetEntry(session)?.placed;

// The open or cursor-line hint, else the PR thread on the cursor's line.
function fixTarget(session: ClientSession): FixItem | null {
  const target = targetHint(session);
  if (target !== undefined) {
    const { hint, line } = target;
    return { path: hint.path, line, quote: hint.quote, text: hintText(hint) };
  }
  const line = cursorLine(session);
  return line === undefined
    ? null
    : (editorContexts.get(session)?.prComments.fixItemAt(line) ?? null);
}

function step(session: ClientSession, direction: 1 | -1, revealFile: RevealFile): void {
  showReviewHints();
  const open = openedHint(session);
  const owner = open !== null && open.owner !== null ? open.owner : undefined;
  const connection =
    owner === undefined ? editorContexts.get(session) : editorContexts.fromEditor(owner);
  const local = connection?.reviewHints.placed() ?? [];
  const positions: HintPosition[] = (reviewHintsFor(session) ?? []).map((hint) => ({
    path: hint.path,
    line: local.find((entry) => entry.hint.id === hint.id)?.line ?? hint.line,
    id: hint.id,
  }));
  const cursor = connection?.editor.getPosition()?.lineNumber;
  const from =
    open !== null
      ? positions.find((position) => position.id === open.id)
      : connection?.model.uri.scheme === SESSION_FILE_SCHEME && cursor !== undefined
        ? { path: sessionUriHostPath(connection.model.uri), line: cursor, id: direction * Infinity }
        : undefined;
  const target = stepHint(positions, from, direction);
  if (target === undefined) {
    notify("info", "There are no review hints.", "review-hints");
    return;
  }
  openHint(session, { id: target.id, owner: null, pinned: true });
  if (local.some((entry) => entry.hint.id === target.id)) return;
  if (activeTabFor(session)?.presentation?.actions()?.revealLine?.(target.path, target.line))
    return;
  revealFile(session, target.path, target.line);
}

/** The web-run review hint commands; Clear runs in Core. */
export function registerReviewHintCommands(revealFile: RevealFile): () => void {
  const fix = (
    session: ClientSession | null,
    send: (session: ClientSession, item: FixItem) => void,
  ) => {
    const item = session === null ? null : fixTarget(session);
    if (session === null || item === null) {
      notify("info", "There's no review hint or PR thread on this line.", "review-hints");
      return;
    }
    openHint(session, null);
    send(session, item);
  };
  const cleanups = [
    ...([1, -1] as const).map((direction) =>
      registerCommand(
        direction === 1 ? CommandIds.reviewHintsNext : CommandIds.reviewHintsPrevious,
        (_args, { session }) => {
          if (session !== null) step(session, direction, revealFile);
        },
      ),
    ),
    registerCommand(CommandIds.reviewHintsToggle, () => toggleReviewHints()),
    registerCommand(CommandIds.reviewHintsDismiss, async (_args, { session }) => {
      const target = session === null ? undefined : targetHint(session);
      if (session === null || target === undefined) {
        notify("info", "There's no review hint on this line.", "review-hints");
        return;
      }
      await dismissHint(session, target.hint.id);
    }),
    registerCommand(CommandIds.reviewHintsFix, (_args, { session }) =>
      fix(session, (owner, item) => fixLine(owner, item, "")),
    ),
    registerCommand(CommandIds.reviewHintsFixWith, (_args, { session }) => fix(session, fixWith)),
    registerCommand(CommandIds.reviewHintsComment, (_args, { session }) => {
      const target = session === null ? undefined : targetEntry(session);
      if (session === null || target === undefined) {
        notify("info", "There's no review hint on this line.", "review-hints");
        return;
      }
      const { connection, placed } = target;
      if (!connection.prComments.comment(placed.line, hintText(placed.hint))) {
        notify("info", "This file isn't part of the session's pull request.", "review-hints");
        return;
      }
      openHint(session, null);
    }),
    registerCommand(CommandIds.reviewHintsClose, (_args, { session }) => {
      if (session === null || openedHint(session) === null) return false;
      openHint(session, null);
    }),
  ];
  return () => {
    for (const cleanup of cleanups) cleanup();
  };
}
