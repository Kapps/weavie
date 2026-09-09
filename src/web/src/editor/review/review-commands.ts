import { selectedSession } from "../../bridge";
import type { CommandCapture, CommandHandler } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import type { EditorController } from "../editor-controller";
import type { InlineDiffActions } from "../inline-diff";
import { activeTabFor } from "../session-store";
import { commandPath } from "../tab-command-bindings";

export type ReviewCommandBinding = readonly [id: string, capture: CommandCapture];

export function reviewCommandBindings(editor: EditorController): ReviewCommandBinding[] {
  const atView =
    (action: keyof InlineDiffActions): CommandCapture =>
    ({ session }) => {
      const tab = session === null ? undefined : activeTabFor(session);
      const presentation = tab?.presentation;
      const run = presentation?.actions()?.[action];
      if (run === undefined && session !== null) {
        if (action === "undoKeep") return () => editor.review.undoKeep(session);
        if (action === "undoRevert") return () => editor.review.undoRevert(session);
        if (action === "redoReview") return () => editor.review.redo(session);
      }
      return () => {
        if (run === undefined || presentation === undefined) return false;
        if (
          presentation.signal.aborted ||
          selectedSession() !== session ||
          activeTabFor(session!) !== tab
        )
          throw new Error("The review connection for this command is no longer displayed.");
        return run();
      };
    };
  const forSession =
    (
      action: (
        session: NonNullable<ReturnType<typeof selectedSession>>,
        path: string | undefined,
        args: unknown,
      ) => ReturnType<CommandHandler>,
    ): CommandCapture =>
    ({ session }, args) => {
      const path =
        commandPath(args) ??
        (session === null ? undefined : activeTabFor(session)?.presentation?.capture().text?.path);
      return () => session !== null && action(session, path, args);
    };
  return [
    ...(
      [
        [CommandIds.nextChange, "nextChange"],
        [CommandIds.prevChange, "prevChange"],
        [CommandIds.acceptChange, "accept"],
        [CommandIds.rejectChange, "reject"],
        [CommandIds.reviewComment, "comment"],
        [CommandIds.reviewNextFile, "nextFile"],
        [CommandIds.reviewPrevFile, "prevFile"],
        [CommandIds.undoKeep, "undoKeep"],
        [CommandIds.undoRevert, "undoRevert"],
        [CommandIds.redoReview, "redoReview"],
      ] satisfies [string, keyof InlineDiffActions][]
    ).map(([id, action]): ReviewCommandBinding => [id, atView(action)]),
    [CommandIds.undoChange, forSession((session) => editor.review.revert(session))],
    [CommandIds.keepFile, forSession((session, path) => editor.review.keepFile(session, path))],
    [CommandIds.revertFile, forSession((session, path) => editor.review.revertFile(session, path))],
    [CommandIds.keepAll, forSession((session) => editor.review.keepAll(session))],
    [
      CommandIds.reviewToggleFile,
      (context, args) => {
        const { session } = context;
        const tab = session === null ? undefined : activeTabFor(session);
        const presentation = tab?.presentation;
        const run = forSession((session, path) => editor.review.toggleFileCollapsed(session, path))(
          context,
          args,
        );
        return () =>
          presentation !== undefined &&
          !presentation.signal.aborted &&
          selectedSession() === session &&
          activeTabFor(session!) === tab &&
          run(args, context);
      },
    ],
    [
      CommandIds.reviewOpen,
      forSession((session, _path, args) => {
        const line = (args as { line?: unknown } | undefined)?.line;
        return editor.openReview(
          session,
          commandPath(args),
          typeof line === "number" ? line : undefined,
        );
      }),
    ],
  ];
}
