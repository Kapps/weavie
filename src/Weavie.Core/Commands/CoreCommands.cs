using System.Globalization;

namespace Weavie.Core.Commands;

/// <summary>
/// Registers Weavie's built-in commands across both worlds and all three triggers (keybinding, palette, MCP).
/// The web binds handlers to the <see cref="CommandLocation.Web"/> ids, the host the <see cref="CommandLocation.Core"/>
/// ones. See <c>docs/specs/commands.md</c>.
/// </summary>
public static class CoreCommands {
	/// <summary>The pane-focus command id; bound to <c>ctrl+1..9</c> and dispatched with <c>{ "index": N }</c>.</summary>
	public const string FocusPaneByIndex = "weavie.pane.focusByIndex";

	/// <summary>Toggles fullscreen for the active pane: it fills the pane area while the session rail stays.</summary>
	public const string ToggleFullscreenPane = "weavie.pane.toggleFullscreen";

	/// <summary>Shows/hides the workspace file browser.</summary>
	public const string ToggleFileBrowser = "weavie.view.toggleFileBrowser";

	/// <summary>Focuses the omnibar in file-search ("Go to File") mode.</summary>
	public const string FocusOmnibarFiles = "weavie.omnibar.focusFiles";

	/// <summary>Focuses the omnibar in command-palette mode.</summary>
	public const string FocusOmnibarCommands = "weavie.omnibar.focusCommands";

	/// <summary>Opens the project-wide content-search ("find in files") panel.</summary>
	public const string FindInFiles = "weavie.search.findInFiles";

	/// <summary>Reopens (restarts) the shell terminal pane.</summary>
	public const string ReopenTerminal = "weavie.terminal.reopen";

	/// <summary>Restarts the Claude pane in place (recovers a crashed / crash-looped Claude).</summary>
	public const string RestartClaude = "weavie.claude.restart";

	/// <summary>Applies a pending update now instead of waiting for the drain gate (kills running shell jobs).</summary>
	public const string RestartForUpdate = "weavie.update.restartNow";

	/// <summary>Copies the focused terminal's selection to the OS clipboard; bound to <c>Ctrl+Shift+C</c> / <c>⌘C</c>.</summary>
	public const string TerminalCopy = "weavie.terminal.copy";

	/// <summary>Pastes the OS clipboard into the focused terminal; bound to <c>Ctrl+V</c> / <c>⌘V</c>.</summary>
	public const string TerminalPaste = "weavie.terminal.paste";

	/// <summary>Clears the focused terminal's scrollback (the right-click "Clear" action).</summary>
	public const string TerminalClear = "weavie.terminal.clear";

	/// <summary>Copies the editor selection to the clipboard (the editor right-click "Copy" action).</summary>
	public const string EditorCopy = "weavie.editor.copy";

	/// <summary>Cuts the editor selection to the clipboard (the editor right-click "Cut" action).</summary>
	public const string EditorCut = "weavie.editor.cut";

	/// <summary>Pastes the clipboard into the editor at the cursor (the editor right-click "Paste" action).</summary>
	public const string EditorPaste = "weavie.editor.paste";

	/// <summary>Toggles Weavie's window (focus it / minimize it); bound by default to the global hotkey <c>ctrl+`</c>.</summary>
	public const string ToggleWindow = "weavie.window.toggle";

	/// <summary>Jumps to the next change hunk in the inline diff.</summary>
	public const string NextChange = "weavie.diff.nextChange";

	/// <summary>Jumps to the previous change hunk in the inline diff.</summary>
	public const string PrevChange = "weavie.diff.prevChange";

	/// <summary>Keeps the current change: a proposed edit (default-mode openDiff) or, in post-turn review, the current hunk (mark reviewed + advance).</summary>
	public const string AcceptChange = "weavie.diff.accept";

	/// <summary>Rejects the current change: a proposed edit (default-mode openDiff) or, in post-turn review, the current hunk (revert it on disk + advance).</summary>
	public const string RejectChange = "weavie.diff.reject";

	/// <summary>Undoes the whole accumulated review set (acceptEdits/bypass mode); Keep-all is the cosmetic counterpart.</summary>
	public const string UndoChange = "weavie.diff.undo";

	/// <summary>Reviews the working tree's diff against a ref (arg <c>ref</c>, or a prompt); bound to <c>$mod+Shift+d</c>.</summary>
	public const string DiffAgainst = "weavie.diff.against";

	/// <summary>Reviews the working tree's diff against HEAD's parent — the last commit plus anything uncommitted.</summary>
	public const string DiffAgainstParent = "weavie.diff.againstParent";

	/// <summary>Reviews the working tree's uncommitted changes — its diff against HEAD.</summary>
	public const string DiffAgainstHead = "weavie.diff.againstHead";

	/// <summary>Jumps into the post-turn review (acceptEdits/bypass) at the first changed file; palette-only, no default keybinding.</summary>
	public const string ReviewOpen = "weavie.review.open";

	/// <summary>Walks to the next changed file in the review set; bound to <c>ctrl+$mod+Right</c>.</summary>
	public const string ReviewNextFile = "weavie.review.nextFile";

	/// <summary>Walks to the previous changed file in the review set; bound to <c>ctrl+$mod+Left</c>.</summary>
	public const string ReviewPrevFile = "weavie.review.prevFile";

	/// <summary>Keeps every hunk in the active review file (mark reviewed + advance); palette/Claude only, scope also reachable via the toolbar picker.</summary>
	public const string KeepFile = "weavie.review.keepFile";

	/// <summary>Reverts every change in the active review file on disk (confirms first); palette/Claude only, scope also reachable via the toolbar picker.</summary>
	public const string RevertFile = "weavie.review.revertFile";

	/// <summary>Keeps the whole accumulated review set (the cosmetic counterpart to Undo All Changes); palette/Claude only.</summary>
	public const string KeepAll = "weavie.review.keepAll";

	/// <summary>Undoes the most recent keep — re-pending its change(s); bound to <c>$mod+Shift+Enter</c>.</summary>
	public const string UndoKeep = "weavie.review.undoKeep";

	/// <summary>Undoes the most recent revert — restoring its change on disk; bound to <c>$mod+Shift+Backspace</c>.</summary>
	public const string UndoRevert = "weavie.review.undoRevert";

	/// <summary>Redoes the most recently undone review action; palette/toolbar only, no default keybinding.</summary>
	public const string RedoReview = "weavie.review.redo";

	/// <summary>Comments on the current line of a PR file under review; palette/toolbar only, no default keybinding.</summary>
	public const string ReviewComment = "weavie.review.comment";

	/// <summary>Closes an editor tab (the active tab, or the one named in <c>path</c>); bound to <c>$mod+w</c>.</summary>
	public const string CloseTab = "weavie.editor.closeTab";

	/// <summary>Activates the next editor tab in visual order, wrapping; bound to <c>ctrl+Tab</c> (editor focus).</summary>
	public const string NextTab = "weavie.editor.nextTab";

	/// <summary>Activates the previous editor tab in visual order, wrapping; bound to <c>ctrl+Shift+Tab</c> (editor focus).</summary>
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

	/// <summary>Reopens the most recently closed editor tab; bound to <c>Ctrl+Shift+T</c>.</summary>
	public const string ReopenClosed = "weavie.editor.reopenClosed";

	/// <summary>Goes back to the previous editor location in the navigation history; bound to <c>Alt+Left</c> and the back mouse button.</summary>
	public const string NavigateBack = "weavie.navigation.back";

	/// <summary>Goes forward to the next editor location in the navigation history; bound to <c>Alt+Right</c> and the forward mouse button.</summary>
	public const string NavigateForward = "weavie.navigation.forward";

	/// <summary>Copies an editor tab's file name to the clipboard (the active tab, or <c>path</c>).</summary>
	public const string CopyTabName = "weavie.editor.copyName";

	/// <summary>Copies an editor tab's repo-relative path to the clipboard (the active tab, or <c>path</c>).</summary>
	public const string CopyTabRelativePath = "weavie.editor.copyRelativePath";

	/// <summary>Copies an editor tab's absolute path to the clipboard (the active tab, or <c>path</c>).</summary>
	public const string CopyTabPath = "weavie.editor.copyPath";

	/// <summary>Opens a new scratch (untitled) editor buffer; bound to <c>$mod+n</c>.</summary>
	public const string NewFile = "weavie.editor.newFile";

	/// <summary>Saves the active editor; a scratch buffer prompts for a name. Bound to <c>$mod+s</c>.</summary>
	public const string SaveFile = "weavie.editor.save";

	/// <summary>Opens the recent-files dropdown in the editor tab bar; bound to <c>$mod+e</c>.</summary>
	public const string OpenRecentFiles = "weavie.editor.recentFiles";

	/// <summary>Toggles the active file between Source (Monaco) and rendered Preview; no-op for types without a preview. Bound to <c>$mod+Shift+v</c>.</summary>
	public const string ToggleEditorPreview = "weavie.editor.togglePreview";

	/// <summary>Opens the active preview's first image/diagram in a full-window lightbox (or advances an open one); bound to <c>$mod+Shift+z</c>.</summary>
	public const string ZoomEmbed = "weavie.editor.zoomEmbed";

	/// <summary>Increases the global font size; bound to <c>Ctrl+=</c> / <c>⌘=</c>.</summary>
	public const string IncreaseFontSize = "weavie.font.increase";

	/// <summary>Decreases the global font size; bound to <c>Ctrl+-</c> / <c>⌘-</c>.</summary>
	public const string DecreaseFontSize = "weavie.font.decrease";

	/// <summary>Resets the global font size to the default; bound to <c>Ctrl+0</c> / <c>⌘0</c>.</summary>
	public const string ResetFontSize = "weavie.font.reset";

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

	/// <summary>Open a different workspace folder via the native folder picker.</summary>
	public const string OpenFolder = "weavie.workspace.openFolder";

	/// <summary>Prompt for an http(s) URL and open it in a web (iframe) tab.</summary>
	public const string OpenUrl = "weavie.workspace.openUrl";

	/// <summary>Has Claude inspect the repo and configure the workspace's knowledge-shaped settings (worktree setup command + test profile) on the user's confirmation; backs the workspace-setup suggestion. Palette-visible, no default keybinding.</summary>
	public const string SetupWorkspace = "weavie.workspace.setup";

	/// <summary>Runs tests for a file via the workspace test profile (args <c>file</c>, optional <c>name</c> for a single test); writes the composed command into the shell pane. The one executor behind the lenses and MCP.</summary>
	public const string RunTests = "weavie.tests.run";

	/// <summary>Runs every test in a file (arg <c>file</c>, or the active editor file); bound to <c>$mod+alt+t</c>.</summary>
	public const string RunTestsInFile = "weavie.tests.runFile";

	/// <summary>Runs the test at the editor cursor (web-resolved to the innermost matched symbol, then dispatched to <see cref="RunTests"/>); bound to <c>$mod+alt+r</c>.</summary>
	public const string RunTestAtCursor = "weavie.tests.runAtCursor";

	/// <summary>Connects a Notion account by validating the user's pasted personal access token. One-time action; palette + Claude only, no default keybinding.</summary>
	public const string ConnectNotion = "weavie.source.connectNotion";

	/// <summary>Opens the focused block of a Notion source tab for in-place editing (web-handled, source-edit.ts).</summary>
	public const string SourceEditBlock = "weavie.source.editBlock";

	/// <summary>Saves the in-progress Notion block edit back to the page (web-handled, source-edit.ts).</summary>
	public const string SourceCommitEdit = "weavie.source.commitEdit";

	/// <summary>Cancels the in-progress Notion block edit, restoring the rendered block (web-handled, source-edit.ts).</summary>
	public const string SourceCancelEdit = "weavie.source.cancelEdit";

	/// <summary>Opens Weavie's captured console output (host stdout/stderr) in a read-only tab, and returns the recent tail to Claude. Diagnostic; palette + Claude, no default keybinding.</summary>
	public const string ViewLogs = "weavie.view.logs";

	/// <summary>Builds a registry pre-loaded with the built-in commands.</summary>
	public static CommandRegistry CreateRegistry() {
		var registry = new CommandRegistry();
		Register(registry);
		return registry;
	}

	/// <summary>Registers the built-in commands into <paramref name="registry"/>.</summary>
	public static void Register(CommandRegistry registry) {
		ArgumentNullException.ThrowIfNull(registry);

		// ctrl+1..9 → focus the Nth pane. Literal ctrl (not $mod) to stay Ctrl on macOS, where Cmd+1..9 collides
		// with app/window shortcuts. Keybinding-only; each default binding carries its own index argument.
		var focusBindings = new List<CommandKeybinding>(9);
		for (int i = 1; i <= 9; i++) {
			string n = i.ToString(CultureInfo.InvariantCulture);
			focusBindings.Add(new CommandKeybinding { Key = $"ctrl+{n}", ArgsJson = $"{{\"index\":{n}}}" });
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

		// Fullscreen the active pane: it fills the whole pane area (the session rail stays). Switching panes keeps
		// fullscreen; toggling again restores the saved layout. Web-handled (pure layout-view state).
		registry.Register(new CommandDefinition {
			Id = ToggleFullscreenPane,
			Title = "Toggle Fullscreen Pane",
			RunsIn = CommandLocation.Web,
			Category = "View",
			Description = "Expand the active pane to fill the window, hiding the other panes (the session rail stays). "
				+ "Switching panes keeps fullscreen; run again to restore the previous layout.",
			Aliases = ["fullscreen", "fullscreen pane", "maximize pane", "toggle fullscreen", "expand pane", "zen mode"],
			DefaultKeybindings = [new CommandKeybinding { Key = "alt+Shift+Enter" }],
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
			Id = FindInFiles,
			Title = "Find in Files",
			RunsIn = CommandLocation.Web,
			Category = "Search",
			Description = "Search the active session's workspace for text in file contents, grouped by file; "
				+ "click or Enter on a result jumps to that line.",
			Aliases = ["find in files", "search files", "search in files", "grep", "search project", "find text"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+Shift+f" }],
		});

		// Open Folder + Open URL. Both web-handled: Open Folder posts the existing menu-action to the host's native
		// picker; Open URL opens a web (iframe) tab. Open Folder takes $mod+Shift+O so $mod+O is free for Open URL.
		registry.Register(new CommandDefinition {
			Id = OpenFolder,
			Title = "Open Folder…",
			RunsIn = CommandLocation.Web,
			Category = "File",
			Description = "Open a different workspace folder (shows the native folder picker).",
			Aliases = ["open folder", "open workspace", "change folder", "pick folder"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+Shift+o" }],
		});

		registry.Register(new CommandDefinition {
			Id = OpenUrl,
			Title = "Open URL…",
			RunsIn = CommandLocation.Web,
			Category = "File",
			Description = "Open an http(s) URL in a web tab — e.g. a local dev server to preview your app.",
			Aliases = ["open url", "open web page", "web tab", "preview url", "open browser tab"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+o" }],
		});

		registry.Register(new CommandDefinition {
			Id = ReopenTerminal,
			Title = "Reopen Terminal",
			RunsIn = CommandLocation.Core,
			Category = "Terminal",
			Description = "Restart the shell terminal pane (kills its scrollback and any running command).",
			Aliases = ["reopen terminal", "restart shell", "reopen shell", "restart terminal"],
		});

		// No default keybinding: a deliberate recovery action, not a hot path. Recovers a Claude pane the
		// crash-loop breaker gave up on — Restart() clears the breaker and respawns.
		registry.Register(new CommandDefinition {
			Id = RestartClaude,
			Title = "Restart Claude",
			RunsIn = CommandLocation.Core,
			Category = "Claude",
			Description = "Restart the Claude pane in place — recovers it after a crash or once it has crashed repeatedly and stopped.",
			Aliases = ["restart claude", "reopen claude", "relaunch claude", "claude crashed"],
		});

		// No default keybinding: only meaningful while an update is pending (the update indicator's
		// button is the primary affordance; the handler fails cleanly otherwise).
		registry.Register(new CommandDefinition {
			Id = RestartForUpdate,
			Title = "Restart Now for Update",
			RunsIn = CommandLocation.Core,
			Category = "Update",
			Description = "Apply the pending update immediately instead of waiting for sessions to go idle — running shell jobs are killed.",
			Aliases = ["restart for update", "apply update", "update now", "restart now"],
		});

		registry.Register(new CommandDefinition {
			Id = RunTests,
			Title = "Run Test",
			RunsIn = CommandLocation.Core,
			Category = "Tests",
			Description = "Run tests for a file using this workspace's test profile — writes the command into the "
				+ "shell pane. Pass 'name' to run a single test, omit it to run the whole file.",
			Aliases = ["run test", "run tests", "run this test", "execute test"],
			ArgsSchemaJson = "{\"file\":{\"type\":\"string\",\"description\":\"Absolute path of the test file\"},"
				+ "\"name\":{\"type\":\"string\",\"description\":\"Composed test name to run a single test; omit to run the whole file\"}}",
		});

		registry.Register(new CommandDefinition {
			Id = RunTestsInFile,
			Title = "Run Tests in File",
			RunsIn = CommandLocation.Core,
			Category = "Tests",
			Description = "Run every test in a file (the given 'file', or the active editor file) via the workspace test profile.",
			Aliases = ["run tests in file", "run file tests", "test this file", "run all tests in file"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+alt+t" }],
			ArgsSchemaJson = "{\"file\":{\"type\":\"string\",\"description\":\"Absolute path of the test file; omit to use the active editor file\"}}",
		});

		// Web-handled: resolves the innermost matched test symbol at the cursor from the lens cache, then dispatches
		// RunTests {file, name}. editorFocused-gated so the chord is free elsewhere.
		registry.Register(new CommandDefinition {
			Id = RunTestAtCursor,
			Title = "Run Test at Cursor",
			RunsIn = CommandLocation.Web,
			Category = "Tests",
			Description = "Run the test at the editor cursor (the innermost test block containing it).",
			Aliases = ["run test at cursor", "run this test", "run current test"],
			When = "editorFocused",
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+alt+r" }],
		});

		// Terminal copy/paste are web-handled (they act on the live xterm selection) but write/read the OS
		// clipboard through the host. Gated terminalFocused so the chords are the editor's elsewhere; the copy
		// handler additionally declines (key falls through) when there's no selection. Copy can't use Ctrl+C
		// (SIGINT), so it takes Ctrl+Shift+C (+ ⌘C); paste has no such conflict and takes the plain $mod+v
		// (Ctrl+V / ⌘V). The off-platform copy chord (Ctrl+Shift+C on mac) is harmless — rare and terminal-gated.
		registry.Register(new CommandDefinition {
			Id = TerminalCopy,
			Title = "Copy",
			RunsIn = CommandLocation.Web,
			Category = "Terminal",
			Description = "Copy the focused terminal's selection to the clipboard. Does nothing when nothing is selected.",
			Aliases = ["copy", "copy selection", "terminal copy", "copy from terminal"],
			DefaultKeybindings = [
				new CommandKeybinding { Key = "ctrl+shift+c" },
				new CommandKeybinding { Key = "cmd+c" },
			],
			When = "terminalFocused",
		});

		registry.Register(new CommandDefinition {
			Id = TerminalPaste,
			Title = "Paste",
			RunsIn = CommandLocation.Web,
			Category = "Terminal",
			Description = "Paste the clipboard's text into the focused terminal.",
			Aliases = ["paste", "paste clipboard", "terminal paste", "paste into terminal"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+v" }],
			// Not on a browser shell: it can't read the clipboard programmatically, so this command (and its
			// palette row) would be a dead end there — Ctrl+V falls through to xterm's native paste instead.
			When = "terminalFocused && !browserShell",
		});

		// Clear the focused terminal's scrollback. Right-click surface only (no chord — many shells own Ctrl+L);
		// palette-visible under terminal focus for discovery.
		registry.Register(new CommandDefinition {
			Id = TerminalClear,
			Title = "Clear",
			RunsIn = CommandLocation.Web,
			Category = "Terminal",
			Description = "Clear the focused terminal's scrollback.",
			Aliases = ["clear", "clear terminal", "clear scrollback", "cls"],
			When = "terminalFocused",
		});

		// Editor clipboard actions: web-handled, they trigger Monaco's own copy/cut/paste so the chords stay
		// Monaco's (no Weavie keybinding to double-handle Ctrl+C/X/V). Right-click surface + palette discovery,
		// gated to editor focus.
		registry.Register(new CommandDefinition {
			Id = EditorCopy,
			Title = "Copy",
			RunsIn = CommandLocation.Web,
			Category = "Edit",
			Description = "Copy the editor selection to the clipboard.",
			Aliases = ["copy", "copy selection", "editor copy"],
			When = "editorFocused",
		});

		registry.Register(new CommandDefinition {
			Id = EditorCut,
			Title = "Cut",
			RunsIn = CommandLocation.Web,
			Category = "Edit",
			Description = "Cut the editor selection to the clipboard.",
			Aliases = ["cut", "cut selection", "editor cut"],
			When = "editorFocused",
		});

		registry.Register(new CommandDefinition {
			Id = EditorPaste,
			Title = "Paste",
			RunsIn = CommandLocation.Web,
			Category = "Edit",
			Description = "Paste the clipboard into the editor at the cursor.",
			Aliases = ["paste", "paste clipboard", "editor paste"],
			When = "editorFocused",
		});

		// Toggle Weavie in/out of the foreground via the GLOBAL hotkey ctrl+` so it fires even when another app
		// is focused (an in-app keydown can't reach an unfocused window). Literal ctrl (not $mod): Cmd+` is the
		// macOS "cycle windows" shortcut. Not in the palette but reachable by Claude over runCommand.
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

		// Inline-diff navigation + actions (the floating diff toolbar), all web-handled. The handlers DECLINE
		// (let the key fall through to the editor) when no diff is active, so these safely reuse familiar editor
		// chords. Each chord carries a per-binding `!terminalFocused` guard so it never fires while a terminal
		// holds focus — Ctrl+Backspace in Claude must delete a word, not revert a hunk. A command-level
		// `diffActive` guard keeps them out of the palette unless a diff/review is actually in play, so an empty
		// workspace doesn't lead with commands that silently no-op (#137).
		registry.Register(new CommandDefinition {
			Id = NextChange,
			Title = "Next Change",
			RunsIn = CommandLocation.Web,
			Category = "Diff",
			When = "diffActive",
			Description = "Jump to the next change in the inline diff.",
			Aliases = ["next change", "next diff", "next hunk", "go to next change"],
			DefaultKeybindings = [new CommandKeybinding { Key = "ctrl+$mod+Down", When = "!terminalFocused" }],
		});

		registry.Register(new CommandDefinition {
			Id = PrevChange,
			Title = "Previous Change",
			RunsIn = CommandLocation.Web,
			Category = "Diff",
			When = "diffActive",
			Description = "Jump to the previous change in the inline diff.",
			Aliases = ["previous change", "prev change", "previous diff", "previous hunk"],
			DefaultKeybindings = [new CommandKeybinding { Key = "ctrl+$mod+Up", When = "!terminalFocused" }],
		});

		registry.Register(new CommandDefinition {
			Id = AcceptChange,
			Title = "Keep Change",
			RunsIn = CommandLocation.Web,
			Category = "Diff",
			When = "diffActive",
			Description = "Keep the proposed edit under review (default-mode openDiff), or — in post-turn review "
				+ "(acceptEdits/bypass) — keep at the toolbar's current scope (this change, file, or all) and advance.",
			Aliases = ["accept change", "keep change", "keep edit", "keep hunk", "accept edit"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+Enter", When = "!terminalFocused" }],
		});

		registry.Register(new CommandDefinition {
			Id = RejectChange,
			Title = "Revert Change",
			RunsIn = CommandLocation.Web,
			Category = "Diff",
			When = "diffActive",
			Description = "Reject the proposed edit under review (default-mode openDiff), or — in post-turn review "
				+ "(acceptEdits/bypass) — revert at the toolbar's current scope (this change, file, or all) and advance.",
			Aliases = ["reject change", "revert hunk", "reject edit", "discard proposal"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+Backspace", When = "!terminalFocused" }],
		});

		registry.Register(new CommandDefinition {
			Id = UndoChange,
			Title = "Undo All Changes",
			RunsIn = CommandLocation.Web,
			Category = "Diff",
			When = "diffActive",
			Description = "Revert the whole accumulated review set on disk (acceptEdits/bypass mode).",
			Aliases = ["undo change", "undo turn", "revert changes", "revert turn", "undo all"],
		});

		// Diff Against: arm the read-only review navigator (the PR-review surface) with the working tree diffed
		// against any ref, from its merge-base with HEAD. Web-handled: the bare command opens a ref prompt (or
		// skips it when invoked with a 'ref' arg, e.g. by Claude); the helpers post their fixed ref directly.
		registry.Register(new CommandDefinition {
			Id = DiffAgainst,
			Title = "Diff Against…",
			RunsIn = CommandLocation.Web,
			Category = "Diff",
			Description = "Review the working tree's diff against a branch, tag, or commit in the inline-diff "
				+ "navigator. Diffs from the ref's merge-base with HEAD, so against a branch it shows only this "
				+ "side's changes. Pass 'ref' to skip the prompt.",
			Aliases = ["diff against", "diff against branch", "compare against", "diff vs", "review diff against", "compare with branch"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+Shift+d" }],
			ArgsSchemaJson = "{\"ref\":{\"type\":\"string\",\"description\":\"Branch, tag, or commit to diff against; omit to prompt\"}}",
		});

		registry.Register(new CommandDefinition {
			Id = DiffAgainstParent,
			Title = "Diff Against Parent",
			RunsIn = CommandLocation.Web,
			Category = "Diff",
			Description = "Review the working tree's diff against HEAD's parent commit — the last commit's changes "
				+ "plus anything uncommitted.",
			Aliases = ["diff against parent", "diff last commit", "review last commit", "show last commit"],
		});

		registry.Register(new CommandDefinition {
			Id = DiffAgainstHead,
			Title = "Diff Against HEAD",
			RunsIn = CommandLocation.Web,
			Category = "Diff",
			Description = "Review the working tree's uncommitted changes (its diff against HEAD) in the inline-diff navigator.",
			Aliases = ["diff against head", "uncommitted changes", "review uncommitted changes", "working tree diff", "diff working tree"],
		});

		// Post-turn review (acceptEdits/bypass): inline in the editor via the diff toolbar, a 2D navigator
		// (Up/Down = hunks, Left/Right = files) on ctrl+$mod — plain Ctrl on Win/Linux, ⌃⌘ on Mac so ⌘+arrows
		// keep their macOS line/document meaning. Web-handled; the handlers DECLINE when no review diff is
		// active, so Ctrl+Left/Right keep their Win/Linux word-nav meaning outside a review.
		registry.Register(new CommandDefinition {
			Id = ReviewOpen,
			Title = "Review Changes",
			RunsIn = CommandLocation.Web,
			Category = "Review",
			Description = "Jump into the post-turn review (acceptEdits/bypass mode) at the first changed file, "
				+ "landed on its first change. Walk hunks with Next/Previous Change and files with Next/Previous File.",
			Aliases = ["review changes", "review turn", "turn review", "review", "changed files"],
		});

		registry.Register(new CommandDefinition {
			Id = ReviewNextFile,
			Title = "Next File (Review)",
			RunsIn = CommandLocation.Web,
			Category = "Review",
			Description = "Walk to the next changed file in the post-turn review set, landed on its first change.",
			Aliases = ["next file in review", "next changed file", "next review file"],
			DefaultKeybindings = [new CommandKeybinding { Key = "ctrl+$mod+Right", When = "!terminalFocused" }],
		});

		registry.Register(new CommandDefinition {
			Id = ReviewPrevFile,
			Title = "Previous File (Review)",
			RunsIn = CommandLocation.Web,
			Category = "Review",
			Description = "Walk to the previous changed file in the post-turn review set, landed on its first change.",
			Aliases = ["previous file in review", "previous changed file", "prev review file"],
			DefaultKeybindings = [new CommandKeybinding { Key = "ctrl+$mod+Left", When = "!terminalFocused" }],
		});

		// File-scoped review actions. Scope now rides the toolbar's sticky picker (Keep/Revert at file scope =
		// Ctrl+Enter/Ctrl+Backspace with the picker on "File"), so these carry no chord of their own — they stay
		// palette + Claude entries. The Shift+Enter/Shift+Backspace chords they used to own are now Undo Keep/Revert.
		registry.Register(new CommandDefinition {
			Id = KeepFile,
			Title = "Keep File (Review)",
			RunsIn = CommandLocation.Web,
			Category = "Review",
			Description = "Keep every change in the active file under review (mark them reviewed) and advance to the next file.",
			Aliases = ["keep file", "keep this file", "accept file", "keep whole file"],
		});

		registry.Register(new CommandDefinition {
			Id = RevertFile,
			Title = "Revert File (Review)",
			RunsIn = CommandLocation.Web,
			Category = "Review",
			Description = "Revert every change in the active file under review back to its turn baseline on disk (confirms first).",
			Aliases = ["revert file", "revert this file", "discard file", "undo file"],
		});

		registry.Register(new CommandDefinition {
			Id = KeepAll,
			Title = "Keep All Changes (Review)",
			RunsIn = CommandLocation.Web,
			Category = "Review",
			Description = "Keep the whole accumulated review set in one action (the commit point — clears the marks "
				+ "and the review undo history).",
			Aliases = ["keep all", "keep all changes", "accept all", "accept turn", "keep everything"],
		});

		// Review undo/redo. The modifier picks direction: Shift+Enter undoes the last keep, Shift+Backspace the
		// last revert (the inverse of the plain Keep/Revert chords). Both carry the `!terminalFocused` guard and
		// decline (fall through) when there's nothing of that kind to undo. Redo is palette/toolbar only.
		registry.Register(new CommandDefinition {
			Id = UndoKeep,
			Title = "Undo Keep (Review)",
			RunsIn = CommandLocation.Web,
			Category = "Review",
			Description = "Undo the most recent Keep — bring its change back into the pending review set.",
			Aliases = ["undo keep", "undo accept", "unkeep", "undo last keep"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+Shift+Enter", When = "!terminalFocused" }],
		});

		registry.Register(new CommandDefinition {
			Id = UndoRevert,
			Title = "Undo Revert (Review)",
			RunsIn = CommandLocation.Web,
			Category = "Review",
			Description = "Undo the most recent Revert — restore its change on disk.",
			Aliases = ["undo revert", "undo reject", "restore reverted", "undo last revert"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+Shift+Backspace", When = "!terminalFocused" }],
		});

		registry.Register(new CommandDefinition {
			Id = RedoReview,
			Title = "Redo Review Action",
			RunsIn = CommandLocation.Web,
			Category = "Review",
			Description = "Re-apply the most recently undone review action (the counterpart to Undo Keep / Undo Revert).",
			Aliases = ["redo review", "redo keep", "redo revert", "redo change"],
		});

		registry.Register(new CommandDefinition {
			Id = ReviewComment,
			Title = "Comment on Line (Review)",
			RunsIn = CommandLocation.Web,
			Category = "Review",
			When = "diffActive",
			Description = "Add a review comment on the current line of a PR file under review (declines outside a PR review).",
			Aliases = ["comment", "add comment", "review comment", "comment on line", "reply"],
		});

		// Editor tabs. closeTab / nextTab / prevTab are gated to editor focus (a tab key shouldn't act while a
		// terminal holds focus). next/prev use literal ctrl (not $mod) — Cmd+Tab is the macOS app switcher — and
		// share the chord with session next/prev under !editorFocused (the exact complement of this guard). The
		// bulk closes + pin take an optional `path` so the context menu targets the right-clicked tab while the
		// palette acts on the active one.
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
			DefaultKeybindings = [new CommandKeybinding { Key = "ctrl+Tab" }],
			When = "editorFocused",
		});

		registry.Register(new CommandDefinition {
			Id = PrevTab,
			Title = "Previous Editor",
			RunsIn = CommandLocation.Web,
			Category = "Editor",
			Description = "Activate the previous editor tab (wraps around).",
			Aliases = ["previous tab", "prev tab", "previous editor", "previous file"],
			DefaultKeybindings = [new CommandKeybinding { Key = "ctrl+Shift+Tab" }],
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

		// The near-universal Ctrl+Shift+T. The handler declines (key falls through) when there's nothing to
		// reopen, so the chord is harmless with no closed tabs.
		registry.Register(new CommandDefinition {
			Id = ReopenClosed,
			Title = "Reopen Closed Editor",
			RunsIn = CommandLocation.Web,
			Category = "Editor",
			Description = "Reopen the most recently closed editor tab.",
			Aliases = ["reopen closed editor", "reopen closed tab", "reopen tab", "restore closed tab", "undo close tab"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+Shift+t" }],
		});

		// Back / forward through visited editor locations — a browser/IDE-style navigation history. Web-handled.
		// The traditional bindings: the back/forward mouse buttons (the MouseBack / MouseForward key tokens,
		// resolved by the web's mousedown resolver) plus Alt+Left / Alt+Right. The Alt chords carry a per-binding
		// `!terminalFocused` guard so a focused terminal keeps its Alt+arrow word-nav; the mouse buttons conflict
		// with nothing, so they bind unguarded. The handlers DECLINE (the event falls through) when there's no
		// history to step to in that direction.
		registry.Register(new CommandDefinition {
			Id = NavigateBack,
			Title = "Go Back",
			RunsIn = CommandLocation.Web,
			Category = "Navigation",
			Description = "Go back to the previous editor location (file + line) in the navigation history. "
				+ "Also driven by the back mouse button.",
			Aliases = ["go back", "navigate back", "back", "previous location", "go to previous location"],
			DefaultKeybindings = [
				new CommandKeybinding { Key = "alt+Left", When = "!terminalFocused" },
				new CommandKeybinding { Key = "MouseBack" },
			],
		});

		registry.Register(new CommandDefinition {
			Id = NavigateForward,
			Title = "Go Forward",
			RunsIn = CommandLocation.Web,
			Category = "Navigation",
			Description = "Go forward to the next editor location (file + line) in the navigation history. "
				+ "Also driven by the forward mouse button.",
			Aliases = ["go forward", "navigate forward", "forward", "next location", "go to next location"],
			DefaultKeybindings = [
				new CommandKeybinding { Key = "alt+Right", When = "!terminalFocused" },
				new CommandKeybinding { Key = "MouseForward" },
			],
		});

		// Copy an editor tab's name / repo-relative / absolute path to the clipboard — the tab menu's Copy
		// submenu. Web-handled (the clipboard write follows the served browser vs native WebView split, like
		// terminal copy); each takes an optional `path` so the menu targets the right-clicked tab while the
		// palette / Claude act on the active one. Gated editorFocused so the palette rows aren't dead with no
		// editor open.
		registry.Register(new CommandDefinition {
			Id = CopyTabName,
			Title = "Copy Name",
			RunsIn = CommandLocation.Web,
			Category = "Editor",
			Description = "Copy the editor tab's file name to the clipboard.",
			Aliases = ["copy name", "copy file name", "copy filename"],
			When = "editorFocused",
			ArgsSchemaJson = tabPathArgs,
		});

		registry.Register(new CommandDefinition {
			Id = CopyTabRelativePath,
			Title = "Copy Relative Path",
			RunsIn = CommandLocation.Web,
			Category = "Editor",
			Description = "Copy the editor tab's path relative to the repository root to the clipboard.",
			Aliases = ["copy relative path", "copy repo path", "copy relative file path"],
			When = "editorFocused",
			ArgsSchemaJson = tabPathArgs,
		});

		registry.Register(new CommandDefinition {
			Id = CopyTabPath,
			Title = "Copy Path",
			RunsIn = CommandLocation.Web,
			Category = "Editor",
			Description = "Copy the editor tab's absolute path to the clipboard.",
			Aliases = ["copy path", "copy absolute path", "copy full path", "copy file path"],
			When = "editorFocused",
			ArgsSchemaJson = tabPathArgs,
		});

		// New File is gated "!terminalFocused" (not "editorFocused") so $mod+n works anywhere except a terminal
		// (where Ctrl+N is readline next-history), including before any tab is open. Save is gated editorFocused
		// so the terminal keeps Ctrl+S = XOFF when it has focus.
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

		// Recent files: a dropdown in the editor tab bar listing frecency-ranked recently-opened files. Web-handled
		// (the dropdown lives in the tab strip). $mod+e is VS Code's quick-open-recent chord and is otherwise free;
		// the per-binding !terminalFocused guard leaves Ctrl+E as the shell's readline "end of line".
		registry.Register(new CommandDefinition {
			Id = OpenRecentFiles,
			Title = "Open Recent File",
			RunsIn = CommandLocation.Web,
			Category = "Navigation",
			Description = "Open the recent-files dropdown in the editor tab bar to reopen a recently-used file.",
			Aliases = ["recent files", "open recent", "recently opened", "reopen recent file"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+e", When = "!terminalFocused" }],
		});

		// Toggle the active file between Source (Monaco) and a rendered Preview (Markdown today). Gated to
		// editorFocused; the handler additionally DECLINES (key falls through to Monaco) for a file type with no
		// preview, so the chord is harmless on, say, a .cs file. The focus-editor re-press (ctrl+<editor#> while
		// the editor is already focused) routes through the same handler.
		registry.Register(new CommandDefinition {
			Id = ToggleEditorPreview,
			Title = "Toggle Preview",
			RunsIn = CommandLocation.Web,
			Category = "Editor",
			Description = "Switch the active file between Source (the Monaco editor) and a rendered Preview "
				+ "(Markdown today). Does nothing for file types without a preview.",
			Aliases = ["toggle preview", "preview", "markdown preview", "render markdown", "show preview", "source view"],
			When = "editorFocused",
		});

		// Zoom a preview embed (image / Mermaid diagram) into a full-window lightbox. The handler DECLINES
		// when the active view has no embeds, so in the Monaco editor the chord falls through (it's redo on
		// some platforms).
		registry.Register(new CommandDefinition {
			Id = ZoomEmbed,
			Title = "Zoom Embed",
			RunsIn = CommandLocation.Web,
			Category = "Editor",
			Description = "Open the current preview's image or Mermaid diagram in a full-window lightbox; run "
				+ "again (or use the arrow keys) to step through the other embeds. Does nothing when no preview "
				+ "with an embed is showing.",
			Aliases = ["zoom embed", "zoom image", "zoom diagram", "enlarge image", "magnify", "lightbox"],
			When = "editorFocused",
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+Shift+z" }],
		});

		// Font zoom (handlers wired in Core by FontCommands): adjust the global font.size setting, which the web
		// applies live to both the editor and terminal. The familiar browser-zoom chords; a matched binding
		// preventDefaults, so the chord changes the app font instead of the page zoom.
		registry.Register(new CommandDefinition {
			Id = IncreaseFontSize,
			Title = "Increase Font Size",
			RunsIn = CommandLocation.Core,
			Category = "View",
			Description = "Increase the editor and terminal font size by one pixel.",
			Aliases = ["increase font size", "zoom in", "bigger text", "larger font", "font bigger"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+=" }, new CommandKeybinding { Key = "$mod+shift+=" }],
		});

		registry.Register(new CommandDefinition {
			Id = DecreaseFontSize,
			Title = "Decrease Font Size",
			RunsIn = CommandLocation.Core,
			Category = "View",
			Description = "Decrease the editor and terminal font size by one pixel.",
			Aliases = ["decrease font size", "zoom out", "smaller text", "smaller font", "font smaller"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+-" }],
		});

		registry.Register(new CommandDefinition {
			Id = ResetFontSize,
			Title = "Reset Font Size",
			RunsIn = CommandLocation.Core,
			Category = "View",
			Description = "Reset the editor and terminal font size to the default.",
			Aliases = ["reset font size", "reset zoom", "default font size", "zoom reset"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+0" }],
		});

		// Theme verb actions (handlers wired in Core by ThemeCommands); the data-shaped override editors +
		// queries stay MCP tools. install/select carry args with no meaningful no-arg palette row, so they're
		// keybinding/runCommand-only; install-from-file is palette-visible (no args → native .vsix picker).
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

		registry.Register(new CommandDefinition {
			Id = SetupWorkspace,
			Title = "Set Up This Workspace with Claude",
			RunsIn = CommandLocation.Core,
			Category = "Workspace",
			Description = "Have Claude inspect the repository and configure this workspace's settings — the command to ready a fresh checkout and how to run its tests — on your confirmation.",
			Aliases = ["set up workspace", "configure workspace", "suggest setup command", "configure test runner", "how to run tests", "worktree setup command"],
		});

		// Connect a Notion account (Core-handled in HostCore.Sources.cs). One-time action — palette + Claude, no
		// default keybinding (like RestartClaude / SetupWorkspace).
		registry.Register(new CommandDefinition {
			Id = ConnectNotion,
			Title = "Connect Notion",
			RunsIn = CommandLocation.Core,
			Category = "Source",
			Description = "Connect your Notion account so you can open Notion docs (read-only). Opens Notion's token "
				+ "page in your browser and a dialog to paste a personal access token — Weavie validates it and saves "
				+ "it for you.",
			Aliases = ["connect notion", "sign in to notion", "authorize notion", "link notion", "add notion"],
		});

		// In-place Notion block editing (web-handled in source-edit.ts): Enter on a focused block opens the inline
		// editor; Enter / Escape while editing save / cancel. Gated by the source-view context keys, so the plain
		// chords stay free everywhere else. See docs/specs/notion-writes.md.
		registry.Register(new CommandDefinition {
			Id = SourceEditBlock,
			Title = "Edit Block",
			RunsIn = CommandLocation.Web,
			Category = "Source",
			Description = "Edit the focused block of the open Notion page in place; saving writes the change back to Notion.",
			Aliases = ["edit block", "edit notion block", "edit source block", "edit notion page"],
			DefaultKeybindings = [new CommandKeybinding { Key = "Enter" }],
			When = "sourceBlockFocused && !sourceEditing",
		});

		registry.Register(new CommandDefinition {
			Id = SourceCommitEdit,
			Title = "Save Block Edit",
			RunsIn = CommandLocation.Web,
			Category = "Source",
			Description = "Save the in-progress Notion block edit back to the page.",
			DefaultKeybindings = [new CommandKeybinding { Key = "Enter" }],
			When = "sourceEditing",
			ShowInPalette = false,
		});

		registry.Register(new CommandDefinition {
			Id = SourceCancelEdit,
			Title = "Cancel Block Edit",
			RunsIn = CommandLocation.Web,
			Category = "Source",
			Description = "Cancel the in-progress Notion block edit, restoring the rendered block.",
			DefaultKeybindings = [new CommandKeybinding { Key = "Escape" }],
			When = "sourceEditing",
			ShowInPalette = false,
		});

		// In-app log viewer (Core-handled in HostCore.Logs.cs): opens the captured console output as a read-only
		// tab and, when Claude runs it, returns the recent tail. Diagnostic — palette + Claude, no keybinding.
		registry.Register(new CommandDefinition {
			Id = ViewLogs,
			Title = "View Logs",
			RunsIn = CommandLocation.Core,
			Category = "View",
			Description = "Open Weavie's captured console output (the host's stdout/stderr) in a read-only tab — the "
				+ "diagnostics log. When Claude runs it, the most recent lines come back too, so it can see errors that "
				+ "would otherwise only appear in the terminal Weavie was launched from.",
			Aliases = ["view logs", "show logs", "open logs", "diagnostics", "stdout", "console output", "log viewer"],
		});

		// Multi-session + worktree commands: Core-handled new/fork/close wired per host via
		// SessionCommands.RegisterHandlers; next/prev/switch are web-handled by the session rail.
		SessionCommands.Register(registry);
	}
}
