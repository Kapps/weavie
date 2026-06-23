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
  reopenTerminal: "weavie.terminal.reopen",
  terminalCopy: "weavie.terminal.copy",
  terminalPaste: "weavie.terminal.paste",
  toggleWindow: "weavie.window.toggle",
  nextChange: "weavie.diff.nextChange",
  prevChange: "weavie.diff.prevChange",
  acceptChange: "weavie.diff.accept",
  rejectChange: "weavie.diff.reject",
  undoChange: "weavie.diff.undo",
  reviewOpen: "weavie.review.open",
  reviewNextFile: "weavie.review.nextFile",
  reviewPrevFile: "weavie.review.prevFile",
  keepFile: "weavie.review.keepFile",
  revertFile: "weavie.review.revertFile",
  keepAll: "weavie.review.keepAll",
  closeTab: "weavie.editor.closeTab",
  nextTab: "weavie.editor.nextTab",
  prevTab: "weavie.editor.prevTab",
  closeAllTabs: "weavie.editor.closeAll",
  closeOtherTabs: "weavie.editor.closeOthers",
  closeTabsToLeft: "weavie.editor.closeToLeft",
  closeTabsToRight: "weavie.editor.closeToRight",
  togglePinTab: "weavie.editor.togglePin",
  newFile: "weavie.editor.newFile",
  saveFile: "weavie.editor.save",
  toggleEditorPreview: "weavie.editor.togglePreview",
  newSessionPrompt: "weavie.session.newPrompt",
  nextSession: "weavie.session.next",
  prevSession: "weavie.session.prev",
  selectSessionByIndex: "weavie.session.selectByIndex",
  loadSession: "weavie.session.load",
  unloadSession: "weavie.session.unload",
  deleteSessionPrompt: "weavie.session.deletePrompt",
  disconnectRemoteAgent: "weavie.session.disconnectRemote",
  removeFromRail: "weavie.session.removeFromRail",
} as const;

declare global {
  interface Window {
    /** Command catalog injected by the host before navigation; absent in plain-browser dev. */
    __WEAVIE_COMMANDS__?: CommandInfo[];
    /** Resolved keybindings injected by the host before navigation; absent in plain-browser dev. */
    __WEAVIE_KEYBINDINGS__?: ResolvedKeybinding[];
  }
}
