using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Layout;
using Weavie.Core.Mcp;
using Weavie.Core.Review.Hints;
using Weavie.Core.Sessions;
using Weavie.Core.Theming;
using Weavie.Core.Workflows;
using Weavie.Workflows;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Test factory for the MCP servers. Production constructors require every dependency explicitly
/// (optional parameters are banned — see the WV0001 analyzer), so tests construct through here and
/// pass only the subset they care about. Defaults are allowed here because the analyzer does not apply
/// to test projects.
/// </summary>
internal static class TestMcp {
	/// <summary>
	/// Builds the IDE server over <paramref name="editor"/>, or with <paramref name="registryMode"/> a registry
	/// whose unspecified stores are isolated in-memory ones. An IDE server takes no registry capability.
	/// </summary>
	internal static McpServer Server(
		string authToken,
		IDiffPresenter presenter,
		IReadOnlyList<string> workspaceFolders,
		string ideName = "weavie",
		SettingsStore? settings = null,
		string? settingsWorkspaceRoot = null,
		bool registryMode = false,
		bool exposeIdeTools = false,
		LayoutStore? layout = null,
		EditorStore? editor = null,
		CommandDispatcher? commands = null,
		KeybindingStore? keybindings = null,
		ThemeOverridesStore? themeOverrides = null,
		ISessionPeers? sessions = null,
		AgentConsultation? agents = null,
		IReviewHintSink? reviewHints = null,
		IWorkflowRegistry? workflows = null) {
		if (registryMode) {
			var defaults = Defaults(
				settings ?? throw new ArgumentNullException(nameof(settings), "A registry needs a settings store."),
				settingsWorkspaceRoot ?? workspaceFolders[0]);
			return new McpServer(authToken, presenter, workspaceFolders, ideName, defaults with {
				ExposeIdeTools = exposeIdeTools,
				Layout = layout ?? defaults.Layout,
				Editor = editor ?? defaults.Editor,
				Commands = commands ?? defaults.Commands,
				Keybindings = keybindings ?? defaults.Keybindings,
				ThemeOverrides = themeOverrides ?? defaults.ThemeOverrides,
				Sessions = sessions ?? defaults.Sessions,
				Agents = agents ?? defaults.Agents,
				ReviewHints = reviewHints ?? defaults.ReviewHints,
				Workflows = workflows ?? defaults.Workflows,
			});
		}
		object?[] registryOnly = [settings, settingsWorkspaceRoot, layout, commands, keybindings, themeOverrides, sessions, agents, reviewHints, workflows];
		return registryOnly.All(value => value is null)
			? McpServer.Ide(authToken, presenter, workspaceFolders, ideName, editor ?? new EditorStore())
			: throw new ArgumentException("An IDE server offers only its editor; pass registryMode: true for registry capabilities.");
	}

	/// <summary>Builds a provider-neutral capability registry with isolated stores.</summary>
	internal static CapabilityRegistryHost Registry(
		string authToken,
		IDiffPresenter presenter,
		IReadOnlyList<string> workspaceFolders,
		SettingsStore settings) =>
		new(new AgentSessionCredential { Token = authToken }, presenter, workspaceFolders, "weavie", Defaults(settings, workspaceFolders[0]));

	// A registry over isolated stores and refusing sinks, without the IDE tools.
	private static McpCapabilities Defaults(SettingsStore settings, string settingsWorkspaceRoot) {
		var fileSystem = new InMemoryFileSystem();
		var commands = CoreCommands.CreateRegistry();
		return new() {
			ExposeIdeTools = false,
			Settings = settings,
			SettingsWorkspaceRoot = settingsWorkspaceRoot,
			Layout = new LayoutStore(fileSystem, LayoutPanes.CreateRegistry(), "/layout.json"),
			Editor = new EditorStore(),
			Commands = new CommandDispatcher(commands),
			Keybindings = new KeybindingStore(commands, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), enableWatcher: false),
			ThemeOverrides = new ThemeOverridesStore(fileSystem, "/theme-overrides.json"),
			Sessions = NoSessionPeers.Instance,
			Agents = AgentConsultation.None,
			ReviewHints = ReviewHintSink.None,
			Workflows = WorkflowRegistry.None,
		};
	}

	/// <summary>The JSON-RPC response carried by an MCP event-stream reply (the event with an <c>id</c>).</summary>
	internal static async Task<JsonDocument> ReadResponseAsync(HttpResponseMessage response) {
		Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
		string body = await response.Content.ReadAsStringAsync();
		foreach (string block in body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)) {
			var document = JsonDocument.Parse(block.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..]);
			if (document.RootElement.TryGetProperty("id", out _)) return document;
			document.Dispose();
		}
		throw new InvalidOperationException($"No JSON-RPC response in: {body}");
	}
}
