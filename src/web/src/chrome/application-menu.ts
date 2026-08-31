import { CommandIds } from "../commands/types";

export interface ApplicationMenuCommand {
  kind: "command";
  commandId: string;
  excludePlatforms?: string[];
}

export interface ApplicationMenuSeparator {
  kind: "separator";
}

export interface ApplicationMenuSubmenu {
  kind: "submenu";
  label: string;
  entries: ApplicationMenuEntry[];
}

export interface ApplicationMenuRecentWorkspaces {
  kind: "recentWorkspaces";
}

export type ApplicationMenuEntry =
  | ApplicationMenuCommand
  | ApplicationMenuSeparator
  | ApplicationMenuSubmenu
  | ApplicationMenuRecentWorkspaces;

export interface ApplicationMenuDefinition {
  id: string;
  label: string;
  entries: ApplicationMenuEntry[];
}

const command = (commandId: string, excludePlatforms?: string[]): ApplicationMenuCommand => ({
  kind: "command",
  commandId,
  ...(excludePlatforms === undefined ? {} : { excludePlatforms }),
});
const separator = (): ApplicationMenuSeparator => ({ kind: "separator" });
const submenu = (label: string, entries: ApplicationMenuEntry[]): ApplicationMenuSubmenu => ({
  kind: "submenu",
  label,
  entries,
});

/**
 * The one curated placement tree for the application menu on every platform. Command labels, availability,
 * dispatch, and effective shortcuts remain authoritative in the active command catalog.
 */
export const APPLICATION_MENUS: ApplicationMenuDefinition[] = [
  {
    id: "file",
    label: "File",
    entries: [
      command(CommandIds.newFile),
      command(CommandIds.focusOmnibarFiles),
      command(CommandIds.openFileByPath),
      command(CommandIds.openRecentFiles),
      separator(),
      command(CommandIds.openFolder),
      { kind: "recentWorkspaces" },
      command(CommandIds.openUrl),
      separator(),
      command(CommandIds.saveFile),
      command(CommandIds.closeTab),
      command(CommandIds.closeWindow),
      command(CommandIds.exit, ["mac"]),
    ],
  },
  {
    id: "go",
    label: "Go",
    entries: [
      command(CommandIds.navBack),
      command(CommandIds.navForward),
      separator(),
      command(CommandIds.goToSymbol),
      command(CommandIds.goToWorkspaceSymbol),
      command(CommandIds.editorGoToDefinition),
      command(CommandIds.editorPeekDefinition),
      command(CommandIds.editorGoToReferences),
      separator(),
      command(CommandIds.prevTab),
      command(CommandIds.nextTab),
      command(CommandIds.prevSession),
      command(CommandIds.nextSession),
    ],
  },
  {
    id: "view",
    label: "View",
    entries: [
      command(CommandIds.focusOmnibarCommands),
      separator(),
      command(CommandIds.toggleFileBrowser),
      command(CommandIds.toggleFullscreenPane),
      command(CommandIds.toggleEditorPreview),
      submenu("Git Blame", [command(CommandIds.toggleBlame), command(CommandIds.showBlame)]),
      submenu("Appearance", [
        command(CommandIds.increaseFontSize),
        command(CommandIds.decreaseFontSize),
        command(CommandIds.resetFontSize),
        separator(),
        command(CommandIds.cycleThemeMode),
      ]),
      separator(),
      command(CommandIds.viewLogs),
    ],
  },
  {
    id: "diff",
    label: "Diff",
    entries: [
      command(CommandIds.reviewOpen),
      separator(),
      command(CommandIds.diffAgainst),
      command(CommandIds.diffAgainstHead),
      command(CommandIds.diffAgainstParent),
      separator(),
      command(CommandIds.prevChange),
      command(CommandIds.nextChange),
      command(CommandIds.acceptChange),
      command(CommandIds.rejectChange),
      command(CommandIds.undoChange),
    ],
  },
  {
    id: "run",
    label: "Run",
    entries: [
      command(CommandIds.runTestsInFile),
      command(CommandIds.runTestAtCursor),
      separator(),
      submenu("Terminal", [
        command(CommandIds.newTerminal),
        command(CommandIds.closeTerminalPrompt),
        command(CommandIds.reopenTerminal),
        separator(),
        command(CommandIds.prevTerminalTab),
        command(CommandIds.nextTerminalTab),
      ]),
      submenu("Agent", [
        command(CommandIds.showSessions),
        command(CommandIds.restartAgent),
        command(CommandIds.manageAcpAgents),
      ]),
    ],
  },
];

/** Every curated command id, recursively, for catalog validation and focused unit tests. */
export function applicationMenuCommandIds(
  menus: ApplicationMenuDefinition[] = APPLICATION_MENUS,
): string[] {
  const ids: string[] = [];
  const visit = (entries: ApplicationMenuEntry[]): void => {
    for (const entry of entries) {
      if (entry.kind === "command") {
        ids.push(entry.commandId);
      } else if (entry.kind === "submenu") {
        visit(entry.entries);
      } else if (entry.kind === "recentWorkspaces") {
        ids.push(CommandIds.openRecentWorkspace);
      }
    }
  };
  for (const menu of menus) {
    visit(menu.entries);
  }
  return ids;
}
