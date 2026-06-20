// The web view of the command system. Mirrors Weavie.Core.Commands' CommandCatalog JSON: the host injects
// the catalog + resolved keybindings as window globals before navigation and re-pushes a { type: "commands" }
// message when the user edits ~/.weavie/keybindings.json. Commands are *declared* in Core; the web binds
// handlers to the web-side ids and resolves keydowns against the keybindings. See docs/specs/commands.md.

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
   * When true this is an OS-level global hotkey: the host registers it with the operating system (so it
   * fires even when Weavie is unfocused) and the web keydown resolver skips it — see keybindings.ts.
   */
  global?: boolean;
}

/** The built-in command ids (kept in sync with CoreCommands.cs), so call sites avoid magic strings. */
export const CommandIds = {
  focusPaneByIndex: "weavie.pane.focusByIndex",
  toggleFileBrowser: "weavie.view.toggleFileBrowser",
  toggleChanges: "weavie.view.toggleChanges",
  focusOmnibarFiles: "weavie.omnibar.focusFiles",
  focusOmnibarCommands: "weavie.omnibar.focusCommands",
  reopenTerminal: "weavie.terminal.reopen",
  toggleWindow: "weavie.window.toggle",
  nextChange: "weavie.diff.nextChange",
  prevChange: "weavie.diff.prevChange",
  acceptChange: "weavie.diff.accept",
  rejectChange: "weavie.diff.reject",
  undoChange: "weavie.diff.undo",
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
  newSessionPrompt: "weavie.session.newPrompt",
  nextSession: "weavie.session.next",
  prevSession: "weavie.session.prev",
  selectSessionByIndex: "weavie.session.selectByIndex",
  loadSession: "weavie.session.load",
  unloadSession: "weavie.session.unload",
  deleteSessionPrompt: "weavie.session.deletePrompt",
} as const;

declare global {
  interface Window {
    /** Command catalog injected by the host before navigation; absent in plain-browser dev. */
    __WEAVIE_COMMANDS__?: CommandInfo[];
    /** Resolved keybindings injected by the host before navigation; absent in plain-browser dev. */
    __WEAVIE_KEYBINDINGS__?: ResolvedKeybinding[];
  }
}
