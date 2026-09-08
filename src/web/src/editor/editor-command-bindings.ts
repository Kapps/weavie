import type * as monaco from "monaco-editor";
import { type ClientSession, selectedSession } from "../bridge";
import { type CommandCapture, registerCapturedCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { editorContexts, type TextEditorConnection } from "./editor-context";
import type { EditorController } from "./editor-controller";

/** Captures the model and selection before chrome takes focus or a command waits in its owner's lane. */
export function captureEditorCommand(session: ClientSession | null) {
  const connection = session === null ? undefined : editorContexts.get(session);
  const selections = connection?.editor.getSelections();
  return (
    action: (
      connection: TextEditorConnection,
      selection: monaco.Selection,
    ) => void | boolean | Promise<void>,
  ) => {
    if (connection === undefined || selections == null || selections.length === 0) return false;
    if (!editorContexts.live(connection) || selectedSession() !== connection.session) {
      throw new Error("The editor connection for this command is no longer displayed.");
    }
    connection.editor.setSelections(selections);
    return action(connection, selections[0]!);
  };
}

export function registerEditorCommands(
  controller: EditorController,
  showMenu: (menu: NonNullable<ReturnType<EditorController["correctSpelling"]>>) => void,
): () => void {
  const bindings: [string, CommandCapture][] = [
    ...[
      [CommandIds.editorCopy, "editor.action.clipboardCopyAction"],
      [CommandIds.editorCut, "editor.action.clipboardCutAction"],
      [CommandIds.editorPaste, "editor.action.clipboardPasteAction"],
      [CommandIds.editorGoToDefinition, "editor.action.revealDefinition"],
      [CommandIds.editorPeekDefinition, "editor.action.peekDefinition"],
      [CommandIds.editorGoToReferences, "editor.action.goToReferences"],
      [CommandIds.editorRename, "editor.action.rename"],
    ].map(([id, action]): [string, CommandCapture] => [
      id!,
      ({ session }) => {
        const run = captureEditorCommand(session);
        return () =>
          run(({ editor }) => {
            editor.focus();
            editor.trigger("weavie-command", action!, null);
          });
      },
    ]),
    [
      CommandIds.runTestAtCursor,
      ({ session }) => {
        const run = captureEditorCommand(session);
        return () =>
          run(async (connection, selection) => {
            await (await import("../tests/test-lens")).runTestAtCursor(connection, selection);
          });
      },
    ],
    [
      CommandIds.reviseSelection,
      ({ session }) => {
        const run = captureEditorCommand(session);
        return () =>
          run((connection, selection) => controller.reviseSelection(connection, selection));
      },
    ],
    [
      CommandIds.showBlame,
      ({ session }) => {
        const run = captureEditorCommand(session);
        return () => run(({ blame }) => blame.showAtCursor());
      },
    ],
    [
      CommandIds.spellCorrect,
      ({ session }) => {
        const run = captureEditorCommand(session);
        return (args) =>
          run(({ spelling }) => {
            const menu = spelling.correct(args);
            if (menu !== null) showMenu(menu);
          });
      },
    ],
    ...(["user", "project"] as const).map((scope): [string, CommandCapture] => [
      scope === "user" ? CommandIds.spellAddUser : CommandIds.spellAddProject,
      ({ session }) => {
        const run = captureEditorCommand(session);
        return (args) => run(({ spelling }) => spelling.add(scope, args));
      },
    ]),
    ...(["back", "forward"] as const).map((direction): [string, CommandCapture] => [
      direction === "back" ? CommandIds.navBack : CommandIds.navForward,
      ({ session }) =>
        () =>
          session !== null && controller.nav[direction](session),
    ]),
  ];
  const cleanups = bindings.map(([id, capture]) => registerCapturedCommand(id, capture));
  return () => {
    for (const cleanup of cleanups) cleanup();
  };
}
