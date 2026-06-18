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
	}
}
