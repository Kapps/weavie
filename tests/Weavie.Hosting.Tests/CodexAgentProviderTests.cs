using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Layout;
using Weavie.Core.Mcp;
using Weavie.Core.Sessions;
using Weavie.Core.Theming;
using Weavie.Hosting.Agents.Codex;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class CodexAgentProviderTests {
	[Fact]
	public async Task CreateSession_WhenIntegrationFails_ReturnsVisibleUnavailableSession() {
		string dir = Path.Combine(Path.GetTempPath(), "weavie-codex-provider-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		InMemoryFileSystem fileSystem = new();
		using var settings = CoreSettings.CreateStore(Path.Combine(dir, "settings.toml"), enableWatcher: false);
		using var path = JsonDocument.Parse("\"codex.exe\"");
		settings.Set("codex.path", path.RootElement);
		var commandRegistry = CoreCommands.CreateRegistry();
		var registry = new CapabilityRegistryHost(
			AgentSessionCredential.Create(),
			FakeDiffPresenter.AlwaysKeep(),
			[dir],
			"weavie",
			settings,
			new LayoutStore(fileSystem, LayoutPanes.CreateRegistry(), "/layout.json"),
			new EditorStore(),
			exposeIdeTools: true,
			new CommandDispatcher(commandRegistry),
			new KeybindingStore(commandRegistry, Path.Combine(dir, "keybindings.json"), enableWatcher: false),
			new ThemeOverridesStore(fileSystem, "/theme-overrides.json"),
			() => "slot-1");
		var provider = new CodexAgentProvider(
			new CodexThreadStore(fileSystem, "/codex-threads.json"),
			(_, _, _) => throw new InvalidOperationException("relay missing"));
		await using var session = Assert.IsAssignableFrom<IStructuredAgentSession>(
			provider.CreateSession(new AgentSessionContext {
				Settings = settings,
				Workspace = dir,
				FileSystem = fileSystem,
				Registry = registry,
				DiffPresenter = FakeDiffPresenter.AlwaysKeep(),
				Editor = new EditorStore(),
				Runtime = new HostRuntimeInfo(HostTransport.Local, Managed: false, "test"),
				Events = new NullAgentEventSink(),
				CurrentSessionId = () => "slot-1",
			}));
		List<AgentPaneMessage> messages = [];
		session.PaneMessage += messages.Add;

		session.Start();

		var error = Assert.Single(messages);
		Assert.Equal("error", error.Type);
		Assert.Equal("codex", error.ProviderId);
		Assert.Equal("relay missing", error.Text);
	}

	private sealed class NullAgentEventSink : IAgentEventSink {
		public AgentEventFeedback Observe(AgentEvent value) => AgentEventFeedback.None;
	}
}
