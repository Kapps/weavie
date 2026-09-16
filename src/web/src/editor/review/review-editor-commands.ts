import { CoreNavigationCommands } from "@codingame/monaco-vscode-api/vscode/vs/editor/browser/coreCommands";
import type { ICodeEditor } from "@codingame/monaco-vscode-api/vscode/vs/editor/browser/editorBrowser";
import { ICodeEditorService } from "@codingame/monaco-vscode-api/vscode/vs/editor/browser/services/codeEditorService.service";
import type { IViewModel } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/viewModel";
import { CommandsRegistry } from "@codingame/monaco-vscode-api/vscode/vs/platform/commands/common/commands";

const commands = [
  CoreNavigationCommands.CursorPageUp,
  CoreNavigationCommands.CursorPageUpSelect,
  CoreNavigationCommands.CursorPageDown,
  CoreNavigationCommands.CursorPageDownSelect,
  CoreNavigationCommands.ScrollLineUp,
  CoreNavigationCommands.ScrollLineDown,
  CoreNavigationCommands.ScrollPageUp,
  CoreNavigationCommands.ScrollPageDown,
  CoreNavigationCommands.ScrollEditorTop,
  CoreNavigationCommands.ScrollEditorBottom,
  CoreNavigationCommands.EditorScroll,
];
const editors = new Map<ICodeEditor, () => IViewModel>();
let registrations: { dispose(): void }[] = [];

/** Retains native commands and keybindings while giving review navigation its actual viewport. */
export function registerReviewEditorCommands(
  editor: ICodeEditor,
  viewModel: () => IViewModel,
): { dispose(): void } {
  if (editors.size === 0) {
    registrations = commands.map((command) => {
      const original = CommandsRegistry.getCommand(command.id);
      if (original === undefined) throw new Error(`Missing editor command: ${command.id}`);
      return CommandsRegistry.registerCommand({
        ...original,
        handler: (accessor, ...args) => {
          const focused = accessor.get(ICodeEditorService).getFocusedCodeEditor();
          const owner = focused === null ? undefined : editors.get(focused);
          if (owner === undefined) return original.handler(accessor, ...args);
          const argument = args[0];
          command.runCoreEditorCommand(
            owner(),
            typeof argument === "object" && argument !== null ? argument : {},
          );
        },
      });
    });
  }
  editors.set(editor, viewModel);
  return {
    dispose: () => {
      editors.delete(editor);
      if (editors.size === 0) {
        for (const registration of registrations) registration.dispose();
        registrations = [];
      }
    },
  };
}
