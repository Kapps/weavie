// The web view of the command system, mirroring Core's CommandCatalog JSON. The host injects the catalog +
// resolved keybindings before navigation and re-pushes { type: "commands" } on a keybindings.json edit.
// Commands are declared in Core; the web binds handlers and resolves keydowns. See docs/specs/commands.md.

export type CommandLocation = "web" | "core";

/** One command in the catalog. */
export interface CommandInfo {
  id: string;
  title: string;
  runsIn: CommandLocation;
  category?: string;
  description: string;
  aliases: string[];
  showInPalette: boolean;
  when?: string;
  /** JSON-Schema properties map for the command's args (informational). */
  argsSchema?: Record<string, unknown>;
  /** The keys this command is currently bound to (raw, e.g. "$mod+1"; the UI formats them). */
  keys: string[];
}

/**
 * The outcome of running a command, returned to the caller via the host's `command-result` reply (or
 * synthesized locally for a web-run command). `data` is an optional command-specific payload. Mirrors Core's
 * `CommandResult`. See docs/specs/command-responses.md.
 */
export interface CommandResult {
  ok: boolean;
  message?: string;
  error?: string;
  data?: unknown;
}

/** One effective key binding after merging defaults with the user file. */
export interface ResolvedKeybinding {
  key: string;
  command: string;
  args?: unknown;
  when?: string;
  /**
   * An OS-level global hotkey: the host registers it (so it fires even when unfocused) and the web keydown
   * resolver skips it. See keybindings.ts.
   */
  global?: boolean;
}

/** Built-in command ids (kept in sync with CoreCommands.cs) so call sites avoid magic strings. */
export const CommandIds = {
  focusPaneByIndex: "weavie.pane.focusByIndex",
  toggleFullscreenPane: "weavie.pane.toggleFullscreen",
  toggleFileBrowser: "weavie.view.toggleFileBrowser",
  focusOmnibarFiles: "weavie.omnibar.focusFiles",
  focusOmnibarCommands: "weavie.omnibar.focusCommands",
  goToSymbol: "weavie.omnibar.goToSymbol",
  goToWorkspaceSymbol: "weavie.omnibar.goToWorkspaceSymbol",
  findInFiles: "weavie.search.findInFiles",
  reopenTerminal: "weavie.terminal.reopen",
  terminalCopy: "weavie.terminal.copy",
  terminalPaste: "weavie.terminal.paste",
  terminalClear: "weavie.terminal.clear",
  editorCopy: "weavie.editor.copy",
  editorCut: "weavie.editor.cut",
  editorPaste: "weavie.editor.paste",
  toggleWindow: "weavie.window.toggle",
  nextChange: "weavie.diff.nextChange",
  prevChange: "weavie.diff.prevChange",
  acceptChange: "weavie.diff.accept",
  rejectChange: "weavie.diff.reject",
  undoChange: "weavie.diff.undo",
  diffAgainst: "weavie.diff.against",
  diffAgainstParent: "weavie.diff.againstParent",
  diffAgainstHead: "weavie.diff.againstHead",
  reviewOpen: "weavie.review.open",
  reviewNextFile: "weavie.review.nextFile",
  reviewPrevFile: "weavie.review.prevFile",
  keepFile: "weavie.review.keepFile",
  revertFile: "weavie.review.revertFile",
  keepAll: "weavie.review.keepAll",
  undoKeep: "weavie.review.undoKeep",
  undoRevert: "weavie.review.undoRevert",
  redoReview: "weavie.review.redo",
  reviewComment: "weavie.review.comment",
  closeTab: "weavie.editor.closeTab",
  nextTab: "weavie.editor.nextTab",
  prevTab: "weavie.editor.prevTab",
  closeAllTabs: "weavie.editor.closeAll",
  closeOtherTabs: "weavie.editor.closeOthers",
  closeTabsToLeft: "weavie.editor.closeToLeft",
  closeTabsToRight: "weavie.editor.closeToRight",
  togglePinTab: "weavie.editor.togglePin",
  reopenClosed: "weavie.editor.reopenClosed",
  navBack: "weavie.navigation.back",
  navForward: "weavie.navigation.forward",
  copyTabName: "weavie.editor.copyName",
  copyTabRelativePath: "weavie.editor.copyRelativePath",
  copyTabPath: "weavie.editor.copyPath",
  newFile: "weavie.editor.newFile",
  saveFile: "weavie.editor.save",
  openRecentFiles: "weavie.editor.recentFiles",
  toggleEditorPreview: "weavie.editor.togglePreview",
  zoomEmbed: "weavie.editor.zoomEmbed",
  runTests: "weavie.tests.run",
  runTestsInFile: "weavie.tests.runFile",
  runTestAtCursor: "weavie.tests.runAtCursor",
  openFolder: "weavie.workspace.openFolder",
  openUrl: "weavie.workspace.openUrl",
  openUrlExternal: "weavie.workspace.openUrlExternal",
  newSessionPrompt: "weavie.session.newPrompt",
  openPr: "weavie.pr.open",
  nextSession: "weavie.session.next",
  prevSession: "weavie.session.prev",
  selectSessionByIndex: "weavie.session.selectByIndex",
  loadSession: "weavie.session.load",
  unloadSession: "weavie.session.unload",
  deleteSession: "weavie.session.delete",
  deleteSessionPrompt: "weavie.session.deletePrompt",
  disconnectRemoteAgent: "weavie.session.disconnectRemote",
  removeFromRail: "weavie.session.removeFromRail",
  restartForUpdate: "weavie.update.restartNow",
  sourceEditBlock: "weavie.source.editBlock",
  sourceCommitEdit: "weavie.source.commitEdit",
  sourceCancelEdit: "weavie.source.cancelEdit",
} as const;

declare global {
  interface Window {
    /** Command catalog injected by the host before navigation; absent in plain-browser dev. */
    __WEAVIE_COMMANDS__?: CommandInfo[];
    /** Resolved keybindings injected by the host before navigation; absent in plain-browser dev. */
    __WEAVIE_KEYBINDINGS__?: ResolvedKeybinding[];
  }
}
