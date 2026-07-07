using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
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
		bool exposeIdeTools = false,
		LayoutStore? layout = null,
		EditorStore? editor = null,
		CommandDispatcher? commands = null,
		KeybindingStore? keybindings = null,
		ThemeOverridesStore? themeOverrides = null,
		Func<string>? currentSessionId = null) =>
		new(authToken, presenter, workspaceFolders, ideName, settings, registryMode, exposeIdeTools, layout, editor, commands, keybindings, themeOverrides, currentSessionId);

	/// <summary>Builds a provider-neutral capability registry with isolated stores.</summary>
	internal static CapabilityRegistryHost Registry(
		string authToken,
		IDiffPresenter presenter,
		IReadOnlyList<string> workspaceFolders,
		SettingsStore settings) {
		var fileSystem = new InMemoryFileSystem();
		var registry = CoreCommands.CreateRegistry();
		return new CapabilityRegistryHost(
			new AgentSessionCredential { Token = authToken },
			presenter,
			workspaceFolders,
			"weavie",
			settings,
			new LayoutStore(fileSystem, LayoutPanes.CreateRegistry(), "/layout.json"),
			new EditorStore(),
			exposeIdeTools: false,
			new CommandDispatcher(registry),
			new KeybindingStore(registry, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), enableWatcher: false),
			new ThemeOverridesStore(fileSystem, "/theme-overrides.json"),
			() => "test-session");
	}
}
