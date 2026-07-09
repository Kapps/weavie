using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Layout;
using Weavie.Core.Mcp;
using Weavie.Core.Sessions;
using Weavie.Core.Shell;
using Weavie.Core.Theming;
using Weavie.Hosting.Agents;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AgentSessionHostTests {
	[Fact]
	public async Task StructuredProvider_DoesNotStartUntilSlotIsKnown() {
		string dir = Path.Combine(Path.GetTempPath(), "weavie-agent-host-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		string slot = string.Empty;
		var fileSystem = new InMemoryFileSystem();
		using var settings = CoreSettings.CreateStore(Path.Combine(dir, "settings.toml"), enableWatcher: false);
		var commandRegistry = CoreCommands.CreateRegistry();
		var bridge = new FakeHostBridge();
		await using var registry = new CapabilityRegistryHost(
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
			() => slot);
		var session = new FakeStructuredSession();
		await using var host = new AgentSessionHost(
			new FakeStructuredProvider(session),
			new AgentSessionContext {
				Settings = settings,
				Workspace = dir,
				FileSystem = fileSystem,
				Registry = registry,
				DiffPresenter = FakeDiffPresenter.AlwaysKeep(),
				Editor = new EditorStore(),
				Runtime = new HostRuntimeInfo(HostTransport.Local, Managed: false, "test"),
				Events = new NullAgentEventSink(),
				CurrentSessionId = () => slot,
			},
			bridge,
			settings,
			new NoopPtyLauncher());

		Assert.False(session.Started);
		slot = "slot-1";
		host.Structured!.Start();

		var message = Assert.Single(bridge.PostedOfType("agent-pane"));
		Assert.Equal("slot-1", message.GetProperty("slot").GetString());
		Assert.Equal("started", message.GetProperty("message").GetProperty("type").GetString());
	}

	private sealed class FakeStructuredProvider(FakeStructuredSession session) : IAgentProvider {
		public AgentProviderInfo Info { get; } = new() {
			Id = "codex",
			Name = "Codex",
			Capabilities = AgentProviderCapabilities.StructuredPane,
			Available = true,
		};

		public IAgentSession CreateSession(AgentSessionContext context) => session;
	}

	private sealed class FakeStructuredSession : IStructuredAgentSession {
		public event Action<AgentPaneMessage>? PaneMessage;

		public bool Started { get; private set; }

		public void Start() {
			Started = true;
			PaneMessage?.Invoke(new AgentPaneMessage { Type = "started", ProviderId = "codex" });
		}

		public void SubmitPrompt(string prompt) => throw new NotSupportedException();

		public void AttachImage(string path) => throw new NotSupportedException();

		public void PrefillPrompt(string prompt) => throw new NotSupportedException();

		public void Interrupt() => throw new NotSupportedException();

		public void Restart() => throw new NotSupportedException();

		public void ResolveApproval(string requestId, string decision) => throw new NotSupportedException();

		public void ResolveInput(string requestId, IReadOnlyDictionary<string, IReadOnlyList<string>> answers) =>
			throw new NotSupportedException();

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	private sealed class NullAgentEventSink : IAgentEventSink {
		public AgentEventFeedback Observe(AgentEvent value) => AgentEventFeedback.None;
	}
}
