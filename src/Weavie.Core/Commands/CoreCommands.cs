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
	}
}
