using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Editor;
using Weavie.Core.Hooks;
using Weavie.Core.Layout;
using Weavie.Core.Mcp;
using Weavie.Core.Theming;

namespace Weavie.Core.Tests;

/// <summary>
/// Test factory for the MCP servers. Production constructors require every dependency explicitly
/// (optional parameters are banned — see the WV0001 analyzer), so tests construct through here and
/// pass only the subset they care about, with the null/seam wiring in one place. Defaults are allowed
/// here because the analyzer does not apply to test projects.
/// </summary>
internal static class TestMcp {
	/// <summary>Builds an <see cref="McpServer"/> with test defaults for every unspecified dependency.</summary>
	internal static McpServer Server(
		string authToken,
		IDiffPresenter presenter,
		IReadOnlyList<string> workspaceFolders,
		string ideName = "weavie",
		SettingsStore? settings = null,
		bool registryMode = false,
		LayoutStore? layout = null,
		EditorStore? editor = null,
		CommandDispatcher? commands = null,
		KeybindingStore? keybindings = null,
		ThemeOverridesStore? themeOverrides = null) =>
		new(authToken, presenter, workspaceFolders, ideName, settings, registryMode, layout, editor, commands, keybindings, themeOverrides);

	/// <summary>Builds an <see cref="IdeIntegration"/> with test defaults for every unspecified dependency.</summary>
	internal static IdeIntegration Ide(
		IDiffPresenter presenter,
		IReadOnlyList<string> workspaceFolders,
		string ideName = "weavie",
		SettingsStore? settings = null,
		LayoutStore? layout = null,
		EditorStore? editor = null,
		CommandDispatcher? commands = null,
		KeybindingStore? keybindings = null,
		ThemeOverridesStore? themeOverrides = null,
		Func<HookRequest, string?>? editLocator = null) =>
		new(presenter, workspaceFolders, ideName, settings, layout, editor, commands, keybindings, themeOverrides, editLocator);
}
