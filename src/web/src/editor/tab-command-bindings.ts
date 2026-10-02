import { type ClientSession, selectedSession } from "../bridge";
import { writeClipboard } from "../clipboard";
import type { CommandCapture, CommandHandler } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { fileIndexFor } from "../files/session-files";
import type { EditorController } from "./editor-controller";
import { basename, repoRelativePath } from "./fs-path";
import { canPreview } from "./preview/preview-registry";
import { activeTabFor } from "./session-store";
import { isFileTab } from "./tab-entry";
import { toggleViewMode } from "./view-mode-store";

export const commandPath = (args: unknown): string | undefined => {
  const path = (args as { path?: unknown } | undefined)?.path;
  return typeof path === "string" ? path : undefined;
};

export function tabCommandBindings(editor: EditorController): [string, CommandCapture][] {
  type Commands = ReturnType<EditorController["tabs"]["capture"]>;
  const bind =
    (factory: (commands: Commands, session: ClientSession) => CommandHandler): CommandCapture =>
    ({ session }, args) =>
      session === null
        ? () => false
        : factory(editor.tabs.capture(session, commandPath(args)), session);
  const file = (
    action: (tab: NonNullable<Commands["target"]>) => ReturnType<CommandHandler>,
  ): CommandCapture =>
    bind(({ target }) => () => {
      if (target === undefined || !isFileTab(target.entry)) return false;
      target.assertLive();
      return action(target);
    });
  return [
    [CommandIds.closeTab, bind((commands) => commands.close)],
    [CommandIds.nextTab, bind((commands) => commands.next)],
    [CommandIds.prevTab, bind((commands) => commands.prev)],
    [CommandIds.closeAllTabs, bind((commands) => commands.closeAll)],
    [CommandIds.closeOtherTabs, bind((commands) => commands.closeOthers)],
    [CommandIds.closeTabsToLeft, bind((commands) => commands.closeToLeft)],
    [CommandIds.closeTabsToRight, bind((commands) => commands.closeToRight)],
    [CommandIds.togglePinTab, bind((commands) => commands.togglePin)],
    [CommandIds.reopenClosed, bind((commands) => commands.reopenClosed)],
    [
      CommandIds.copyTabName,
      file((tab) => {
        writeClipboard(basename(tab.entry.path));
      }),
    ],
    [
      CommandIds.copyTabRelativePath,
      file((tab) => {
        const root = fileIndexFor(tab.session).root;
        writeClipboard(root === null ? tab.entry.path : repoRelativePath(root, tab.entry.path));
      }),
    ],
    [
      CommandIds.copyTabPath,
      file((tab) => {
        writeClipboard(tab.entry.path);
      }),
    ],
    [CommandIds.saveFile, file((tab) => editor.save(tab))],
    [
      CommandIds.toggleEditorPreview,
      file((tab) => {
        if (!canPreview(tab.entry.path)) return false;
        toggleViewMode(tab.session, tab.entry.path);
        return Promise.resolve().then(async () => {
          const presentation = await tab.wait(tab.signal);
          if (selectedSession() === tab.session && activeTabFor(tab.session) === tab)
            presentation.focus();
        });
      }),
    ],
  ];
}

export function captureTabCommands(
  editor: EditorController,
  session: ClientSession,
  path: string | undefined,
): Map<string, CommandHandler> {
  return new Map(
    tabCommandBindings(editor).map(([id, capture]) => [id, capture({ session }, { path })]),
  );
}
