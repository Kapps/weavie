import type { ClientSession } from "../../bridge";
import type { CommandHandler } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import type { EditorController } from "../editor-controller";

export type ReviewCommandBinding = readonly [id: string, handler: CommandHandler];

const pathArg = (args: unknown): string | undefined => {
  const path = (args as { path?: unknown } | undefined)?.path;
  return typeof path === "string" ? path : undefined;
};

/** Binds review commands to their captured session owner. UI navigation additionally requires that owner visible. */
export function reviewCommandBindings(
  editor: Pick<EditorController, "inline" | "openReview" | "review">,
  selectedSession: () => ClientSession | null,
): ReviewCommandBinding[] {
  const selected = (session: ClientSession | null): session is ClientSession =>
    session !== null && session === selectedSession();
  return [
    [
      CommandIds.nextChange,
      (_args, { session }) => selected(session) && editor.inline.nextChange(),
    ],
    [
      CommandIds.prevChange,
      (_args, { session }) => selected(session) && editor.inline.prevChange(),
    ],
    [CommandIds.acceptChange, (_args, { session }) => selected(session) && editor.inline.accept()],
    [CommandIds.rejectChange, (_args, { session }) => selected(session) && editor.inline.reject()],
    [
      CommandIds.undoChange,
      (_args, { session }) => session !== null && editor.review.revert(session),
    ],
    [
      CommandIds.keepFile,
      (args, { session }) => session !== null && editor.review.keepFile(session, pathArg(args)),
    ],
    [
      CommandIds.revertFile,
      (args, { session }) => session !== null && editor.review.revertFile(session, pathArg(args)),
    ],
    [
      CommandIds.keepAll,
      (_args, { session }) => session !== null && editor.review.keepAll(session),
    ],
    [
      CommandIds.reviewComment,
      (_args, { session }) => selected(session) && editor.inline.comment(),
    ],
    [
      CommandIds.undoKeep,
      (_args, { session }) =>
        session !== null &&
        (selected(session) ? editor.inline.undoKeep() : editor.review.undoKeep(session)),
    ],
    [
      CommandIds.undoRevert,
      (_args, { session }) =>
        session !== null &&
        (selected(session) ? editor.inline.undoRevert() : editor.review.undoRevert(session)),
    ],
    [
      CommandIds.redoReview,
      (_args, { session }) => session !== null && editor.review.redo(session),
    ],
    [
      CommandIds.reviewOpen,
      (args, { session }) => {
        if (!selected(session)) {
          return false;
        }
        const line = (args as { line?: unknown } | undefined)?.line;
        return editor.openReview(
          session,
          pathArg(args),
          typeof line === "number" ? line : undefined,
        );
      },
    ],
    [
      CommandIds.reviewToggleMode,
      (_args, { session }) => selected(session) && editor.review.toggleMode(session),
    ],
    [
      CommandIds.reviewToggleFile,
      (args, { session }) =>
        selected(session) && editor.review.toggleFileCollapsed(session, pathArg(args)),
    ],
    [
      CommandIds.reviewNextFile,
      (_args, { session }) => selected(session) && editor.inline.nextFile(),
    ],
    [
      CommandIds.reviewPrevFile,
      (_args, { session }) => selected(session) && editor.inline.prevFile(),
    ],
  ];
}
