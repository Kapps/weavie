import type { ClientSession } from "../../bridge";
import { registerCommand } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import { notify } from "../../notify/notify";
import { editorContexts, type TextEditorConnection } from "../editor-context";
import { type FixItem, fixLine, fixWith } from "../FixPrompt";
import { reviewItems } from "../review-items/review-items";
import type { PlacedHint } from "./hint-anchor";
import { dismissHint, hintText, openedHint, openHint } from "./review-hints-store";

const cursorLine = (session: ClientSession): number | undefined =>
  editorContexts.get(session)?.editor.getPosition()?.lineNumber;

// The open hint, else the one on the cursor's line, with the editor binding showing it.
function targetEntry(
  session: ClientSession,
): { connection: TextEditorConnection; placed: PlacedHint } | undefined {
  const open = openedHint(session);
  const owner = open?.owner ?? null;
  const connection =
    owner !== null ? editorContexts.fromEditor(owner) : editorContexts.get(session);
  const line = cursorLine(session);
  const placed = connection?.reviewHints
    .placed()
    .find((entry) => (owner !== null ? entry.hint.id === open?.id : entry.line === line));
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

/** The web-run review hint commands; Clear and Toggle run in Core. */
export function registerReviewHintCommands(): () => void {
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
    // Escape closes the open hint, else clears the outlined thread, so the next step starts from the cursor.
    registerCommand(CommandIds.reviewHintsClose, (_args, { session }) => {
      if (session === null) return false;
      if (openedHint(session) === null) return reviewItems.clearThread(session);
      openHint(session, null);
    }),
  ];
  return () => {
    for (const cleanup of cleanups) cleanup();
  };
}
