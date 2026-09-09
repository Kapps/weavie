import type * as monaco from "monaco-editor";
import { type ClientSession, selectedSession } from "../bridge";
import type { ContextMenuContent, ContextMenuState } from "../chrome/ContextMenu";
import {
  type CommandHandler,
  captureCommandRunnerFor,
  registerCapturedCommand,
} from "../commands/registry";
import { CommandIds } from "../commands/types";
import { editorContexts, type TextEditorConnection } from "./editor-context";
import type { EditorController } from "./editor-controller";
import { SESSION_FILE_SCHEME } from "./session-uri-scheme";

/** Captures the model and selection before chrome takes focus or a command waits in its owner's lane. */
export function captureEditorCommand(session: ClientSession | null) {
  const connection = session === null ? undefined : editorContexts.get(session);
  return captureConnectionCommand(connection);
}

export function captureConnectionCommand(connection: TextEditorConnection | undefined) {
  const selections = connection?.editor.getSelections();
  const presentation = connection?.tab.presentation;
  return (
    action: (
      connection: TextEditorConnection,
      selection: monaco.Selection,
    ) => void | boolean | Promise<void>,
  ) => {
    if (connection === undefined || selections == null || selections.length === 0) return false;
    if (
      presentation?.signal.aborted ||
      !editorContexts.displayed(connection) ||
      selectedSession() !== connection.session
    ) {
      throw new Error("The editor connection for this command is no longer displayed.");
    }
    connection.editor.setSelections(selections);
    return action(connection, selections[0]!);
  };
}

export function createEditorCommands(
  controller: EditorController,
  showMenu: (menu: ContextMenuState) => void,
) {
  type Run = ReturnType<typeof captureConnectionCommand>;
  type Factory = (run: Run) => CommandHandler;
  const mutations = new Set<string>([
    CommandIds.editorCut,
    CommandIds.editorPaste,
    CommandIds.editorRename,
    CommandIds.reviseSelection,
  ]);
  const enabled = (id: string, connection: TextEditorConnection): boolean =>
    !mutations.has(id) ||
    (connection.editor.getRawOptions().readOnly !== true &&
      (id !== CommandIds.reviseSelection || connection.model.uri.scheme === SESSION_FILE_SCHEME));
  const bindings: [string, Factory][] = [
    ...[
      [CommandIds.editorCopy, "editor.action.clipboardCopyAction"],
      [CommandIds.editorCut, "editor.action.clipboardCutAction"],
      [CommandIds.editorPaste, "editor.action.clipboardPasteAction"],
      [CommandIds.editorGoToDefinition, "editor.action.revealDefinition"],
      [CommandIds.editorPeekDefinition, "editor.action.peekDefinition"],
      [CommandIds.editorGoToReferences, "editor.action.goToReferences"],
      [CommandIds.editorRename, "editor.action.rename"],
    ].map(([id, action]): [string, Factory] => [
      id!,
      (run) => {
        return () =>
          run(({ editor }) => {
            editor.focus();
            editor.trigger("weavie-command", action!, null);
          });
      },
    ]),
    [
      CommandIds.runTestAtCursor,
      (run) => {
        return () =>
          run(async (connection, selection) => {
            await (await import("../tests/test-lens")).runTestAtCursor(connection, selection);
          });
      },
    ],
    [
      CommandIds.reviseSelection,
      (run) => {
        return () =>
          run((connection, selection) => controller.reviseSelection(connection, selection));
      },
    ],
    [
      CommandIds.showBlame,
      (run) => {
        return () => run(({ blame }) => blame.showAtCursor());
      },
    ],
    [
      CommandIds.spellCorrect,
      (run) => {
        return (args) =>
          run((connection) => {
            const menu = connection.spelling.correct(args);
            if (menu !== null) showOwnedMenu(connection, menu);
          });
      },
    ],
    ...(["user", "project"] as const).map((scope): [string, Factory] => [
      scope === "user" ? CommandIds.spellAddUser : CommandIds.spellAddProject,
      (run) => {
        return (args) => run(({ spelling }) => spelling.add(scope, args));
      },
    ]),
  ];
  const bind = (id: string, factory: Factory, run: Run): CommandHandler =>
    factory((action) =>
      run((connection, selection) => {
        if (!enabled(id, connection))
          throw new Error(
            id === CommandIds.reviseSelection
              ? "Revise requires an editable file."
              : "This document is read-only.",
          );
        return action(connection, selection);
      }),
    );
  function capture(connection: TextEditorConnection): Map<string, CommandHandler> {
    const run = captureConnectionCommand(connection);
    return new Map(bindings.map(([id, factory]) => [id, bind(id, factory, run)]));
  }
  function showOwnedMenu(connection: TextEditorConnection, content: ContextMenuContent): void {
    showMenu({
      ...content,
      runCommand: captureCommandRunnerFor(connection.session, capture(connection)),
    });
  }
  return {
    capture,
    openMenu(connection: TextEditorConnection, x: number, y: number): void {
      const spelling = connection.spelling.menuAt(x, y);
      showOwnedMenu(connection, {
        ...spelling,
        entries: [
          ...spelling.entries,
          ...[
            CommandIds.editorGoToDefinition,
            CommandIds.editorPeekDefinition,
            CommandIds.editorGoToReferences,
            CommandIds.editorRename,
          ].map((commandId) => ({
            commandId,
            disabled: !enabled(commandId, connection),
          })),
          { kind: "separator" },
          {
            commandId: CommandIds.reviseSelection,
            disabled: !enabled(CommandIds.reviseSelection, connection),
          },
          { kind: "separator" },
          ...[CommandIds.editorCut, CommandIds.editorCopy, CommandIds.editorPaste].map(
            (commandId) => ({
              commandId,
              disabled: !enabled(commandId, connection),
            }),
          ),
          { kind: "separator" },
          { commandId: CommandIds.focusOmnibarCommands, label: "Command Palette" },
        ],
      });
    },
    register(): () => void {
      const cleanups = bindings.map(([id, factory]) =>
        registerCapturedCommand(id, ({ session }) =>
          bind(id, factory, captureEditorCommand(session)),
        ),
      );
      for (const direction of ["back", "forward"] as const) {
        cleanups.push(
          registerCapturedCommand(
            direction === "back" ? CommandIds.navBack : CommandIds.navForward,
            ({ session }) =>
              () =>
                session !== null && controller.nav[direction](session),
          ),
        );
      }
      return () => {
        for (const cleanup of cleanups) cleanup();
      };
    },
  };
}
