using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Layout;
using Weavie.Core.Mcp;
using Weavie.Core.Shell;
using Weavie.Core.Theming;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class HostSessionAgentImageTests : IDisposable {
	private readonly string _dir = Path.Combine(Path.GetTempPath(), "weavie-host-session-image-tests", Guid.NewGuid().ToString("N"));

	public HostSessionAgentImageTests() {
		Directory.CreateDirectory(_dir);
	}

	public void Dispose() {
		try {
			Directory.Delete(_dir, recursive: true);
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
	}

	[Fact]
	public async Task SendAgentImagePath_SubmitsPathToStructuredProvider() {
		var structured = new RecordingStructuredSession();
		var commandRegistry = CoreCommands.CreateRegistry();
		using var settings = CoreSettings.CreateStore(Path.Combine(_dir, "settings.toml"), enableWatcher: false);
		await using var session = CreateSession(structured, settings, commandRegistry);

		session.SendAgentImagePath(Path.Combine(_dir, "pasted", "paste-1.png"));

		Assert.Equal([Path.Combine(_dir, "pasted", "paste-1.png")], structured.Images);
	}

	[Fact]
	public async Task RestartAgent_RestartsStructuredProvider() {
		var structured = new RecordingStructuredSession();
		var commandRegistry = CoreCommands.CreateRegistry();
		using var settings = CoreSettings.CreateStore(Path.Combine(_dir, "settings.toml"), enableWatcher: false);
		await using var session = CreateSession(structured, settings, commandRegistry);

		session.RestartAgent();

		Assert.Equal(1, structured.Restarts);
		Assert.Equal(0, structured.Interruptions);
	}

	private HostSession CreateSession(RecordingStructuredSession structured, SettingsStore settings, CommandRegistry commandRegistry) =>
		new(
			new FakeHostBridge(),
			settings,
			new LayoutStore(new Weavie.Core.FileSystem.LocalFileSystem(), LayoutPanes.CreateRegistry(), Path.Combine(_dir, "layout.json")),
			_dir,
			Path.Combine(_dir, "scratch"),
			Path.Combine(_dir, "pasted"),
			"slot-1",
			commandRegistry,
			new KeybindingStore(commandRegistry, Path.Combine(_dir, "keybindings.json"), enableWatcher: false),
			new ThemeOverridesStore(new Weavie.Core.FileSystem.LocalFileSystem(), Path.Combine(_dir, "theme-overrides.json")),
			new NoopPtyLauncher(),
			new FakeStructuredProvider(structured),
			new HostRuntimeInfo(HostTransport.Local, Managed: false, "test"));

	private sealed class FakeStructuredProvider(RecordingStructuredSession session) : IAgentProvider {
		public AgentProviderInfo Info { get; } = new() {
			Id = "codex",
			Name = "Codex",
			Capabilities = AgentProviderCapabilities.StructuredPane,
			Available = true,
		};

		public IAgentSession CreateSession(AgentSessionContext context) => session;
	}

	private sealed class RecordingStructuredSession : IStructuredAgentSession {
		public event Action<AgentPaneMessage>? PaneMessage;

		public List<string> Prompts { get; } = [];

		public List<string> Images { get; } = [];

		public int Interruptions { get; private set; }

		public int Restarts { get; private set; }

		public void Start() => PaneMessage?.Invoke(new AgentPaneMessage { Type = "started", ProviderId = "codex" });

		public void SubmitPrompt(string prompt) => Prompts.Add(prompt);

		public void AttachImage(string path) => Images.Add(path);

		public void PrefillPrompt(string prompt) { }

		public void Interrupt() => Interruptions++;

		public void Restart() => Restarts++;

		public void ResolveApproval(string requestId, string decision) { }

		public void ResolveInput(string requestId, IReadOnlyDictionary<string, IReadOnlyList<string>> answers) { }

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}
