namespace Weavie.Core.Mcp;

// The tools/list JSON entries advertised to Claude, grouped by capability. The constructor stitches the
// relevant groups into one {"tools":[...]} payload depending on the server mode (IDE vs registry) and which
// stores were wired. Kept here as data, separate from the server logic in McpServer.cs.
public sealed partial class McpServer {
	// IDE RPC tools. openDiff is the star (blocking review); the rest give Claude IDE context.
	private const string IdeToolEntries =
		"""
          {"name":"openDiff","description":"Open an editable diff for the user to review proposed changes to a file. Blocks until the user accepts (FILE_SAVED) or rejects (DIFF_REJECTED).","inputSchema":{"type":"object","properties":{"old_file_path":{"type":"string"},"new_file_path":{"type":"string"},"new_file_contents":{"type":"string"},"tab_name":{"type":"string"}},"required":["old_file_path","new_file_path","new_file_contents","tab_name"]}},
          {"name":"openFile","description":"Open/reveal a file in the editor.","inputSchema":{"type":"object","properties":{"filePath":{"type":"string"},"preview":{"type":"boolean"},"startText":{"type":"string"},"endText":{"type":"string"}},"required":["filePath"]}},
          {"name":"getWorkspaceFolders","description":"Get the workspace folders open in the IDE.","inputSchema":{"type":"object","properties":{}}},
          {"name":"getOpenEditors","description":"Get the list of open editor tabs.","inputSchema":{"type":"object","properties":{}}},
          {"name":"getCurrentSelection","description":"Get the current text selection in the active editor.","inputSchema":{"type":"object","properties":{}}},
          {"name":"getDiagnostics","description":"Get language diagnostics from the IDE.","inputSchema":{"type":"object","properties":{"uri":{"type":"string"}}}},
          {"name":"close_tab","description":"Close a tab by name.","inputSchema":{"type":"object","properties":{"tab_name":{"type":"string"}},"required":["tab_name"]}},
          {"name":"closeAllDiffTabs","description":"Close all open diff tabs.","inputSchema":{"type":"object","properties":{}}}
        """;

	// Settings tools (the Claude-facing editing surface), advertised only when a SettingsStore is wired.
	private const string SettingsToolEntries =
		"""
          {"name":"listSettings","description":"List all weavie settings with each one's current value, source (environment/userFile/default), default, description, aliases, and any allowed values. Call this FIRST to find the exact key before changing a setting.","inputSchema":{"type":"object","properties":{}}},
          {"name":"getSetting","description":"Get one weavie setting's resolved value and where it came from (environment/userFile/default). This reflects what the running app has actually loaded — prefer it over reading ~/.weavie config files on disk, which are only the persisted layer and can diverge from the live app.","inputSchema":{"type":"object","properties":{"key":{"type":"string"}},"required":["key"]}},
          {"name":"setSetting","description":"Change a weavie setting. Call listSettings first to find the exact key; never guess keys. 'value' should match the setting's declared type (string/bool/int/path); int and bool values may be sent as a JSON number/boolean or as a string (e.g. 16 or \"16\", true or \"true\").","inputSchema":{"type":"object","properties":{"key":{"type":"string"},"value":{}},"required":["key","value"]}}
        """;

	// Layout tools (model-facing), advertised on the registry server only when a LayoutStore is wired.
	private const string LayoutToolEntries =
		"""
          {"name":"getLayout","description":"Get the current weavie window layout as a JSON tree of nested row/column splits and leaf panes.","inputSchema":{"type":"object","properties":{}}},
          {"name":"setLayout","description":"Replace the weavie window layout. 'root' is a layout tree where each node is a split (type 'split', with 'dir' 'row' or 'column', a 'weights' number array, and a 'children' node array) or a pane (type 'pane', with a unique 'id' and a 'kind'). Pane kinds: editor, terminal:claude, terminal:shell. Weights are relative. Optionally set 'focused' to a pane id. Call getLayout first to see the current shape.","inputSchema":{"type":"object","properties":{"root":{"type":"object"},"focused":{"type":"string"}},"required":["root"]}}
        """;

	// Command tools (model-facing), advertised on the registry server only when a CommandDispatcher is wired.
	private const string CommandToolEntries =
		"""
          {"name":"listCommands","description":"List all weavie commands (actions like focusing a pane, toggling the diff layout, or reopening the terminal) with each one's id, title, category, description, aliases, and current keybinding(s). Call this FIRST to find the exact id before running a command.","inputSchema":{"type":"object","properties":{}}},
          {"name":"runCommand","description":"Run a weavie command by id. Call listCommands first to find the exact id; never guess ids. 'args' is an optional object whose shape depends on the command (e.g. {\"index\":3} to focus the third pane).","inputSchema":{"type":"object","properties":{"id":{"type":"string"},"args":{"type":"object"}},"required":["id"]}}
        """;

	// Theme tools (model-facing) — the Claude-facing theming surface, advertised on the registry server only
	// when a ThemeOverridesStore is wired. These are the data-shaped operations: read the theme/overrides
	// (listThemes/describeTheme) and edit individual override colors (set/transform/remove). The VERB actions —
	// install, install-from-file, select, undo, reset — are COMMANDS (CoreCommands + ThemeCommands), run via
	// runCommand so they're also reachable from the palette + keybindings; listCommands documents them. The
	// override tools act on the ACTIVE theme; its overrides persist in ~/.weavie/theme-overrides.json.
	// applyThemeTransform.amount is schema-less so the embedded claude may send it as a number or a string.
	private const string ThemeToolEntries =
		"""
          {"name":"listThemes","description":"List the available color themes (built-in + installed), each with its id, label, type, and whether it is the active theme. To SWITCH the active theme, run the weavie.theme.select command (runCommand); to INSTALL one, run weavie.theme.install (Open VSX) or weavie.theme.installFromFile (a local .vsix). Call this FIRST to find a theme id.","inputSchema":{"type":"object","properties":{}}},
          {"name":"describeTheme","description":"Describe the active color theme: its id, label, and the ordered list of color overrides currently layered on it. This is the live source of truth for what the running app has loaded — use it to answer \"what theme/overrides are set\", and prefer it over reading ~/.weavie/theme-overrides.json, which is only the persisted layer and can diverge from the live app. To clear all overrides run the weavie.theme.reset command; to pop the last one, weavie.theme.undoOverride.","inputSchema":{"type":"object","properties":{}}},
          {"name":"setThemeOverride","description":"Override one entry's color and/or font style on the active theme. Provide 'value' (a hex color, e.g. #00ff00 or #00000080), 'fontStyle', or both. 'fontStyle' is a space-separated subset of italic, bold, underline, strikethrough (or empty string to clear inherited styles) and applies ONLY to the syntax tables (it's rejected for workbench colors). 'table' chooses what 'key' means: omit it (or 'colors') for a workbench color id (e.g. editor.background, terminal.ansiRed, focusBorder) — color only; 'semanticTokenColors' to style SYNTAX by semantic token type (e.g. key 'keyword', 'function', 'class', 'variable.readonly', 'parameter', 'property'); or 'tokenColors' for a raw TextMate scope (e.g. 'keyword.control', 'string', 'comment'). Prefer 'semanticTokenColors' for languages with an LSP. So to make variables italic: key 'variable', table 'semanticTokenColors', fontStyle 'italic'. Use applyThemeTransform to shift the whole theme at once.","inputSchema":{"type":"object","properties":{"key":{"type":"string"},"value":{"type":"string"},"fontStyle":{"type":"string"},"table":{"type":"string"}},"required":["key"]}},
          {"name":"applyThemeTransform","description":"Shift many of the active theme's colors at once (no need to set them individually). 'op' is one of darken, lighten, saturate, desaturate, contrast; 'amount' is a number from 0 to 1 (e.g. 0.2 = 20%; may be a number or a string). 'target' scopes which colors move: 'all' (default), 'colors' (chrome/editor/terminal backgrounds only), 'tokenColors' or 'semanticTokenColors' (one syntax table), or 'syntax' (both syntax tables). e.g. op 'saturate' amount 0.15 target 'syntax' makes the CODE colors 15% more vivid without touching backgrounds; op 'darken' amount 0.2 target 'all' makes everything 20% darker.","inputSchema":{"type":"object","properties":{"op":{"type":"string"},"amount":{},"target":{"type":"string"}},"required":["op","amount"]}},
          {"name":"removeThemeOverride","description":"Remove the color override(s) for one 'key' from the active theme.","inputSchema":{"type":"object","properties":{"key":{"type":"string"}},"required":["key"]}}
        """;
}
