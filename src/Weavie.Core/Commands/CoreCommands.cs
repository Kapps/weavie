using System.Globalization;

namespace Weavie.Core.Commands;

/// <summary>
/// Registers Weavie's built-in commands. The initial set spans both worlds and all three triggers
/// (keybinding, palette, MCP): pane focus (the migrated Ctrl+1–9), the diff-layout toggle, the omnibar
/// focus commands, and reopening the terminal. Core registers these at startup; the web binds handlers
/// to the <see cref="CommandLocation.Web"/> ids and the host binds the <see cref="CommandLocation.Core"/>
/// ones. Future plugins contribute definitions the same way. See <c>docs/specs/commands.md</c>.
/// </summary>
public static class CoreCommands {
	/// <summary>The pane-focus command id; bound to <c>$mod+1..9</c> and dispatched with <c>{ "index": N }</c>.</summary>
	public const string FocusPaneByIndex = "weavie.pane.focusByIndex";

	/// <summary>Shows/hides the workspace file browser.</summary>
	public const string ToggleFileBrowser = "weavie.view.toggleFileBrowser";

	/// <summary>Shows/hides the session changes panel.</summary>
	public const string ToggleChanges = "weavie.view.toggleChanges";

	/// <summary>Focuses the omnibar in file-search ("Go to File") mode.</summary>
	public const string FocusOmnibarFiles = "weavie.omnibar.focusFiles";

	/// <summary>Focuses the omnibar in command-palette mode.</summary>
	public const string FocusOmnibarCommands = "weavie.omnibar.focusCommands";

	/// <summary>Reopens (restarts) the shell terminal pane. The one Core-side command in the initial set.</summary>
	public const string ReopenTerminal = "weavie.terminal.reopen";

	/// <summary>Toggles Weavie's window (focus it / minimize it); bound by default to the global hotkey <c>ctrl+`</c>.</summary>
	public const string ToggleWindow = "weavie.window.toggle";

	/// <summary>Jumps to the next change hunk in the inline diff.</summary>
	public const string NextChange = "weavie.diff.nextChange";

	/// <summary>Jumps to the previous change hunk in the inline diff.</summary>
	public const string PrevChange = "weavie.diff.prevChange";

	/// <summary>Accepts the active inline diff (keep a proposed edit, or clear the turn's markers).</summary>
	public const string AcceptChange = "weavie.diff.accept";

	/// <summary>Rejects the active inline diff proposal (default-mode review only).</summary>
	public const string RejectChange = "weavie.diff.reject";

	/// <summary>Undoes the current turn's inline changes (acceptEdits/bypass mode).</summary>
	public const string UndoChange = "weavie.diff.undo";

	/// <summary>Closes an editor tab (the active tab, or the one named in <c>path</c>); bound to <c>$mod+w</c>.</summary>
	public const string CloseTab = "weavie.editor.closeTab";

	/// <summary>Activates the next editor tab in visual order, wrapping; bound to <c>$mod+Tab</c>.</summary>
	public const string NextTab = "weavie.editor.nextTab";

	/// <summary>Activates the previous editor tab in visual order, wrapping; bound to <c>$mod+Shift+Tab</c>.</summary>
	public const string PrevTab = "weavie.editor.prevTab";

	/// <summary>Closes all non-pinned editor tabs.</summary>
	public const string CloseAllTabs = "weavie.editor.closeAll";

	/// <summary>Closes every non-pinned editor tab except the target (the active tab, or <c>path</c>).</summary>
	public const string CloseOtherTabs = "weavie.editor.closeOthers";

	/// <summary>Closes non-pinned editor tabs to the left of the target (the active tab, or <c>path</c>).</summary>
	public const string CloseTabsToLeft = "weavie.editor.closeToLeft";

	/// <summary>Closes non-pinned editor tabs to the right of the target (the active tab, or <c>path</c>).</summary>
	public const string CloseTabsToRight = "weavie.editor.closeToRight";

	/// <summary>Pins or unpins an editor tab (the active tab, or <c>path</c>).</summary>
	public const string TogglePinTab = "weavie.editor.togglePin";

	/// <summary>Opens a new scratch (untitled) editor buffer; bound to <c>$mod+n</c>.</summary>
	public const string NewFile = "weavie.editor.newFile";

	/// <summary>Saves the active editor; a scratch buffer prompts for a name. Bound to <c>$mod+s</c>.</summary>
	public const string SaveFile = "weavie.editor.save";

	/// <summary>Installs a color theme from the Open VSX registry (args <c>namespace</c>/<c>name</c>/<c>version</c>).</summary>
	public const string InstallTheme = "weavie.theme.install";

	/// <summary>Installs a color theme from a local <c>.vsix</c> file (arg <c>path</c>, or a native picker if omitted).</summary>
	public const string InstallThemeFromFile = "weavie.theme.installFromFile";

	/// <summary>Switches the theme for a polarity and flips the mode to match (arg <c>id</c>).</summary>
	public const string SelectTheme = "weavie.theme.select";

	/// <summary>Cycles the appearance mode system → light → dark → system.</summary>
	public const string CycleThemeMode = "weavie.theme.cycleMode";

	/// <summary>Pops the most recent color override on the active theme.</summary>
	public const string UndoThemeOverride = "weavie.theme.undoOverride";

	/// <summary>Clears all color overrides on the active theme.</summary>
	public const string ResetTheme = "weavie.theme.reset";

	/// <summary>Builds a registry pre-loaded with the built-in commands.</summary>
	public static CommandRegistry CreateRegistry() {
		var registry = new CommandRegistry();
		Register(registry);
		return registry;
	}

	/// <summary>Registers the built-in commands into <paramref name="registry"/>.</summary>
	public static void Register(CommandRegistry registry) {
		ArgumentNullException.ThrowIfNull(registry);

		// $mod+1..9 → focus the Nth pane in layout order. Keybinding-only: "focus pane index 3" is not a
		// meaningful palette row without context (nice per-pane entries are a follow-up). Each default
		// binding carries its own index argument.
		var focusBindings = new List<CommandKeybinding>(9);
		for (int i = 1; i <= 9; i++) {
			string n = i.ToString(CultureInfo.InvariantCulture);
			focusBindings.Add(new CommandKeybinding { Key = $"$mod+{n}", ArgsJson = $"{{\"index\":{n}}}" });
		}

		registry.Register(new CommandDefinition {
			Id = FocusPaneByIndex,
			Title = "Focus Pane by Number",
			RunsIn = CommandLocation.Web,
			Category = "View",
			Description = "Move keyboard focus to the Nth pane (1-based, in layout order).",
			Aliases = ["focus pane", "switch pane", "go to pane"],
			DefaultKeybindings = focusBindings,
			ShowInPalette = false,
			ArgsSchemaJson = "{\"index\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"1-based pane number in layout order\"}}",
		});

		registry.Register(new CommandDefinition {
			Id = ToggleFileBrowser,
			Title = "Toggle File Browser",
			RunsIn = CommandLocation.Web,
			Category = "View",
			Description = "Show or hide the workspace file browser.",
			Aliases = ["file browser", "files panel", "explorer", "toggle files"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+b" }],
		});

		registry.Register(new CommandDefinition {
			Id = ToggleChanges,
			Title = "Toggle Changes Panel",
			RunsIn = CommandLocation.Web,
			Category = "View",
			Description = "Show or hide the session changes panel (files changed this session).",
			Aliases = ["changes", "changes panel", "diff panel", "toggle changes"],
		});

		registry.Register(new CommandDefinition {
			Id = FocusOmnibarFiles,
			Title = "Go to File",
			RunsIn = CommandLocation.Web,
			Category = "Navigation",
			Description = "Focus the omnibar to quickly open a file by name.",
			Aliases = ["go to file", "open file", "quick open", "find file"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+p" }],
		});

		registry.Register(new CommandDefinition {
			Id = FocusOmnibarCommands,
			Title = "Show All Commands",
			RunsIn = CommandLocation.Web,
			Category = "Navigation",
			Description = "Open the command palette in the omnibar.",
			Aliases = ["command palette", "show commands", "run command"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+Shift+p" }],
		});

		registry.Register(new CommandDefinition {
			Id = ReopenTerminal,
			Title = "Reopen Terminal",
			RunsIn = CommandLocation.Core,
			Category = "Terminal",
			Description = "Restart the shell terminal pane (kills its scrollback and any running command).",
			Aliases = ["reopen terminal", "restart shell", "reopen shell", "restart terminal"],
		});

		// Toggle Weavie's window in/out of the foreground. Bound by default to the GLOBAL hotkey ctrl+`
		// (Global = true): the host registers it with the OS so it fires even when another app is focused —
		// the point of the command (an in-app keydown can't reach an unfocused window). Pressing it focuses
		// Weavie when it's behind, and — when it's already in front — hands focus back to the previously
		// focused window, dropping Weavie behind (Windows; macOS hides the app). No minimize. ctrl+` is used
		// literally (not $mod) so it's the same on Win + macOS, where Cmd+` is already the system "cycle
		// windows" shortcut. Not in the palette — toggling the window you're already typing in is meaningless
		// there — but reachable by Claude over runCommand.
		registry.Register(new CommandDefinition {
			Id = ToggleWindow,
			Title = "Toggle Weavie Window",
			RunsIn = CommandLocation.Core,
			Category = "View",
			Description = "Toggle the Weavie window: bring it to the foreground when another app is in front, "
				+ "or hand focus back to the previously focused window (dropping Weavie behind) when it's already focused.",
			Aliases = ["toggle weavie", "show or hide weavie", "focus weavie", "hide weavie", "bring to front", "raise window"],
			DefaultKeybindings = [new CommandKeybinding { Key = "ctrl+`", Global = true }],
			ShowInPalette = false,
		});

		// Inline-diff navigation + actions (the floating diff toolbar). All web-handled; the toolbar buttons
		// and these keybindings/palette entries route to the same handlers. The web handlers DECLINE (let the
		// key fall through to the editor) when no diff is active, so these reuse familiar editor chords safely:
		// $mod+Down/Up navigate hunks, $mod+Enter accepts, $mod+Backspace rejects (Cursor-style). Undo is left
		// unbound (a whole-turn revert is destructive — bind it deliberately).
		registry.Register(new CommandDefinition {
			Id = NextChange,
			Title = "Next Change",
			RunsIn = CommandLocation.Web,
			Category = "Diff",
			Description = "Jump to the next change in the inline diff.",
			Aliases = ["next change", "next diff", "next hunk", "go to next change"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+Down" }],
		});

		registry.Register(new CommandDefinition {
			Id = PrevChange,
			Title = "Previous Change",
			RunsIn = CommandLocation.Web,
			Category = "Diff",
			Description = "Jump to the previous change in the inline diff.",
			Aliases = ["previous change", "prev change", "previous diff", "previous hunk"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+Up" }],
		});

		registry.Register(new CommandDefinition {
			Id = AcceptChange,
			Title = "Accept Change",
			RunsIn = CommandLocation.Web,
			Category = "Diff",
			Description = "Keep the proposed edit under review, or clear the current turn's inline markers.",
			Aliases = ["accept change", "keep change", "keep edit", "accept edit"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+Enter" }],
		});

		registry.Register(new CommandDefinition {
			Id = RejectChange,
			Title = "Reject Change",
			RunsIn = CommandLocation.Web,
			Category = "Diff",
			Description = "Reject the proposed edit under review (default-mode openDiff).",
			Aliases = ["reject change", "reject edit", "discard proposal"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+Backspace" }],
		});

		registry.Register(new CommandDefinition {
			Id = UndoChange,
			Title = "Undo Change",
			RunsIn = CommandLocation.Web,
			Category = "Diff",
			Description = "Revert the current turn's changes (acceptEdits/bypass mode).",
			Aliases = ["undo change", "undo turn", "revert changes", "revert turn"],
		});

		// Editor tabs. closeTab / nextTab / prevTab carry the keyboard bindings and are gated to editor focus
		// (a tab key shouldn't act while a terminal holds focus). next/prev DECLINE when there are <2 tabs, so
		// $mod+Tab still falls through to the editor. The bulk closes + pin are palette- and context-menu-driven
		// (no ergonomic chord-free key, and the resolver has no chord support); they take an optional `path` so
		// the tab context menu can target the right-clicked tab while the palette acts on the active one.
		string tabPathArgs = "{\"path\":{\"type\":\"string\",\"description\":\"Absolute path of the target tab; omit to act on the active tab\"}}";

		registry.Register(new CommandDefinition {
			Id = CloseTab,
			Title = "Close Editor",
			RunsIn = CommandLocation.Web,
			Category = "Editor",
			Description = "Close the active editor tab (or the tab named in 'path').",
			Aliases = ["close tab", "close editor", "close file"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+w" }],
			When = "editorFocused",
			ArgsSchemaJson = tabPathArgs,
		});

		registry.Register(new CommandDefinition {
			Id = NextTab,
			Title = "Next Editor",
			RunsIn = CommandLocation.Web,
			Category = "Editor",
			Description = "Activate the next editor tab (wraps around).",
			Aliases = ["next tab", "next editor", "next file"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+Tab" }],
			When = "editorFocused",
		});

		registry.Register(new CommandDefinition {
			Id = PrevTab,
			Title = "Previous Editor",
			RunsIn = CommandLocation.Web,
			Category = "Editor",
			Description = "Activate the previous editor tab (wraps around).",
			Aliases = ["previous tab", "prev tab", "previous editor", "previous file"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+Shift+Tab" }],
			When = "editorFocused",
		});

		registry.Register(new CommandDefinition {
			Id = CloseAllTabs,
			Title = "Close All Editors",
			RunsIn = CommandLocation.Web,
			Category = "Editor",
			Description = "Close all editor tabs except pinned ones.",
			Aliases = ["close all tabs", "close all editors", "close all files"],
		});

		registry.Register(new CommandDefinition {
			Id = CloseOtherTabs,
			Title = "Close Other Editors",
			RunsIn = CommandLocation.Web,
			Category = "Editor",
			Description = "Close every editor tab except the target one and pinned tabs.",
			Aliases = ["close other tabs", "close others", "close other editors"],
			ArgsSchemaJson = tabPathArgs,
		});

		registry.Register(new CommandDefinition {
			Id = CloseTabsToLeft,
			Title = "Close Editors to the Left",
			RunsIn = CommandLocation.Web,
			Category = "Editor",
			Description = "Close non-pinned editor tabs to the left of the target tab.",
			Aliases = ["close tabs to the left", "close left", "close editors to the left"],
			ArgsSchemaJson = tabPathArgs,
		});

		registry.Register(new CommandDefinition {
			Id = CloseTabsToRight,
			Title = "Close Editors to the Right",
			RunsIn = CommandLocation.Web,
			Category = "Editor",
			Description = "Close non-pinned editor tabs to the right of the target tab.",
			Aliases = ["close tabs to the right", "close right", "close editors to the right"],
			ArgsSchemaJson = tabPathArgs,
		});

		registry.Register(new CommandDefinition {
			Id = TogglePinTab,
			Title = "Pin / Unpin Editor",
			RunsIn = CommandLocation.Web,
			Category = "Editor",
			Description = "Pin or unpin an editor tab. Pinned tabs are compact, stay furthest-left, and survive "
				+ "Close All / Close Others.",
			Aliases = ["pin tab", "unpin tab", "pin editor", "unpin editor", "toggle pin"],
			ArgsSchemaJson = tabPathArgs,
		});

		// New File / Save. New File opens a scratch (untitled) buffer; it's gated to "!terminalFocused" rather
		// than "editorFocused" so $mod+n works from anywhere except a terminal (where Ctrl+N is readline's
		// next-history) — including when no tab is open yet. Save is gated to editorFocused: a scratch buffer
		// prompts for a real name (a native save dialog), a real file is already autosaved so $mod+s is a no-op
		// that just consumes the key (so the terminal keeps Ctrl+S = XOFF when it has focus).
		registry.Register(new CommandDefinition {
			Id = NewFile,
			Title = "New File",
			RunsIn = CommandLocation.Web,
			Category = "Editor",
			Description = "Open a new scratch (untitled) editor buffer. It persists like any open file; saving "
				+ "prompts for a name and location.",
			Aliases = ["new file", "new scratch", "untitled", "create file"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+n" }],
			When = "!terminalFocused",
		});

		registry.Register(new CommandDefinition {
			Id = SaveFile,
			Title = "Save",
			RunsIn = CommandLocation.Web,
			Category = "Editor",
			Description = "Save the active editor. Real files autosave continuously; a scratch (untitled) buffer "
				+ "prompts for a name and location.",
			Aliases = ["save", "save file", "save as", "save editor"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+s" }],
			When = "editorFocused",
		});

		// Theme verb actions (handlers wired in Core by ThemeCommands over the app-global theme stores). These
		// are the theming actions that became commands so they're reachable from the palette, a keybinding, and
		// Claude's runCommand alike; the data-shaped override editors + queries stay MCP tools. install/select
		// carry args and have no meaningful no-arg palette row, so they're keybinding/runCommand-only;
		// install-from-file IS palette-visible (no args → it opens a native .vsix picker).
		registry.Register(new CommandDefinition {
			Id = InstallTheme,
			Title = "Install Theme from Open VSX",
			RunsIn = CommandLocation.Core,
			Category = "Theme",
			Description = "Install a VS Code color theme from the Open VSX registry by 'namespace' (publisher) "
				+ "and 'name' (extension), optionally a 'version'. e.g. namespace 'dracula-theme' name "
				+ "'theme-dracula'. After installing, run weavie.theme.select to switch to it.",
			Aliases = ["install theme", "add theme", "install from open vsx", "download theme"],
			ShowInPalette = false,
			ArgsSchemaJson = "{\"namespace\":{\"type\":\"string\"},\"name\":{\"type\":\"string\"},\"version\":{\"type\":\"string\"}}",
		});

		registry.Register(new CommandDefinition {
			Id = InstallThemeFromFile,
			Title = "Install Theme from File…",
			RunsIn = CommandLocation.Core,
			Category = "Theme",
			Description = "Install a VS Code color theme from a local .vsix file. With no 'path', opens a file "
				+ "picker to choose one; pass 'path' (an absolute .vsix path) to install without prompting.",
			Aliases = ["install theme from file", "install vsix", "install local theme", "open vsix", "install theme from disk"],
			ArgsSchemaJson = "{\"path\":{\"type\":\"string\",\"description\":\"Absolute path to a .vsix file; omit to choose interactively\"}}",
		});

		registry.Register(new CommandDefinition {
			Id = SelectTheme,
			Title = "Select Theme",
			RunsIn = CommandLocation.Core,
			Category = "Theme",
			Description = "Switch to a color theme. 'id' must be a built-in or installed theme id (use the listThemes "
				+ "tool to see them; never guess). A light theme is stored as your light theme and a dark theme as "
				+ "your dark theme, and the appearance mode flips to match so it shows immediately. Overrides are "
				+ "remembered per theme.",
			Aliases = ["select theme", "switch theme", "change theme", "set theme", "use theme", "activate theme"],
			ShowInPalette = false,
			ArgsSchemaJson = "{\"id\":{\"type\":\"string\"}}",
		});

		registry.Register(new CommandDefinition {
			Id = CycleThemeMode,
			Title = "Cycle Theme Mode",
			RunsIn = CommandLocation.Core,
			Category = "Theme",
			Description = "Cycle the appearance mode: system (match OS) → light → dark → system. Light mode shows "
				+ "your light theme (theme.light), dark shows your dark theme (theme.dark), system follows the OS.",
			Aliases = ["cycle theme mode", "toggle light dark", "toggle dark mode", "switch appearance", "light dark mode", "toggle theme mode"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+Shift+m" }],
		});

		registry.Register(new CommandDefinition {
			Id = UndoThemeOverride,
			Title = "Undo Theme Override",
			RunsIn = CommandLocation.Core,
			Category = "Theme",
			Description = "Undo the most recent color override on the active theme (pop the last set/transform).",
			Aliases = ["undo theme override", "undo last color", "revert theme tweak", "undo color change"],
		});

		registry.Register(new CommandDefinition {
			Id = ResetTheme,
			Title = "Reset Theme Overrides",
			RunsIn = CommandLocation.Core,
			Category = "Theme",
			Description = "Clear ALL color overrides on the active theme, returning it to its authored colors.",
			Aliases = ["reset theme", "clear theme overrides", "restore theme defaults", "remove all overrides"],
		});

		// Multi-session + worktree commands. Declarations live here so every trigger sees them; the
		// Core-handled new/fork/close are wired per host via SessionCommands.RegisterHandlers over the host's
		// ISessionHost, while next/prev/switch are web-handled by the session rail.
		SessionCommands.Register(registry);
	}
}
