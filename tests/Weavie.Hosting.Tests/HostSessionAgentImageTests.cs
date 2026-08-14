using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Layout;
using Weavie.Core.Mcp;
using Weavie.Core.Shell;
using Weavie.Core.Theming;
using Weavie.Hosting.Messaging;
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
	public async Task SendAgentPrompt_SubmitsAtomicInputToStructuredProvider() {
		var structured = new RecordingStructuredSession();
		var commandRegistry = CoreCommands.CreateRegistry();
		using var settings = CoreSettings.CreateStore(Path.Combine(_dir, "settings.toml"), enableWatcher: false);
		await using var session = CreateSession(structured, settings, commandRegistry);

		session.SendAgentPrompt("hello");

		var submission = Assert.Single(structured.Submissions);
		Assert.Equal("hello", submission.Text);
		Assert.Empty(submission.Attachments);
	}

	[Fact]
	public async Task InitialInput_WaitsForIdleAndSubmitsTextAndImagesExactlyOnce() {
		var structured = new RecordingStructuredSession();
		var commandRegistry = CoreCommands.CreateRegistry();
		using var settings = CoreSettings.CreateStore(Path.Combine(_dir, "settings.toml"), enableWatcher: false);
		await using var session = CreateSession(structured, settings, commandRegistry);
		string imagePath = Path.Combine(_dir, "initial.png");

		session.QueueInitialInput(Input("start here", new AgentInputAttachment {
			Id = "image-1",
			Mime = "image/png",
			Path = imagePath,
		}));
		session.Status.Observe(new AgentToolStarting(new AgentMutation.None()));
		Assert.Empty(structured.Submissions);

		session.Status.Observe(new AgentSessionStarted("startup"));
		session.Status.Observe(new AgentSessionStarted("resume"));

		var submission = Assert.Single(structured.Submissions);
		Assert.Equal("start here", submission.Text);
		var attachment = Assert.Single(submission.Attachments);
		Assert.Equal("image-1", attachment.Id);
		Assert.Equal("image/png", attachment.Mime);
		Assert.Equal(imagePath, attachment.Path);
	}

	[Fact]
	public async Task InitialInput_IsDiscardedWithSession() {
		var structured = new RecordingStructuredSession();
		var commandRegistry = CoreCommands.CreateRegistry();
		using var settings = CoreSettings.CreateStore(Path.Combine(_dir, "settings.toml"), enableWatcher: false);
		var session = CreateSession(structured, settings, commandRegistry);
		session.QueueInitialInput(Input("do not send"));

		await session.DisposeAsync();
		session.Status.Observe(new AgentSessionStarted("startup"));

		Assert.Empty(structured.Submissions);
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

	private static AgentTurnSubmission Input(
		string text,
		params AgentInputAttachment[] attachments) =>
		new() {
			Id = "initial",
			Text = text,
			Attachments = attachments,
		};

	private HostSession CreateSession(
		RecordingStructuredSession structured,
		SettingsStore settings,
		CommandRegistry commandRegistry) {
		var endpoint = new HostMessageRouter(
			new FakeHostBridge(),
			new InlineUiDispatcher(),
			_ => { }).OpenSession(
			new SessionAddress("slot-1", Guid.NewGuid().ToString("n")));
		var session = new HostSession(
			endpoint,
			settings,
			new LayoutStore(new Weavie.Core.FileSystem.LocalFileSystem(), LayoutPanes.CreateRegistry(), Path.Combine(_dir, "layout.json")),
			_dir,
			Path.Combine(_dir, "scratch"),
			Path.Combine(_dir, "pasted"),
			Path.Combine(_dir, "agent-pane.json"),
			commandRegistry,
			new KeybindingStore(commandRegistry, Path.Combine(_dir, "keybindings.json"), enableWatcher: false),
			new ThemeOverridesStore(new Weavie.Core.FileSystem.LocalFileSystem(), Path.Combine(_dir, "theme-overrides.json")),
			new Weavie.Core.Corrections.CorrectionCorpus(new Weavie.Core.FileSystem.LocalFileSystem(), Path.Combine(_dir, "corrections.jsonl")),
			new NoopPtyLauncher(),
			new FakeStructuredProvider(structured),
			new HostRuntimeInfo(HostTransport.Local, Managed: false, "test"),
			() => false,
			(_, accept) => accept(),
			(_, _) => { });
		session.ActivateOwnedRuntimeAndMessages();
		return session;
	}

	private sealed class FakeStructuredProvider(RecordingStructuredSession session) : IAgentProvider {
		public AgentProviderInfo Info { get; } = new() {
			Id = "structured",
			Name = "Structured",
			Capabilities = AgentProviderCapabilities.StructuredPane,
			Available = true,
		};

		public IAgentSession CreateSession(AgentSessionContext context) => session;
	}

	private sealed class RecordingStructuredSession : IStructuredAgentSession {
		public event Action<AgentPaneMessage>? PaneMessage;
		public event Action<IReadOnlyList<AgentPaneMessage>>? PaneSnapshot { add { } remove { } }

		public List<AgentTurnSubmission> Submissions { get; } = [];

		public int Interruptions { get; private set; }

		public int Restarts { get; private set; }

		public void Start() => PaneMessage?.Invoke(new AgentPaneMessage { Type = "started", ProviderId = "structured" });

		public void Submit(AgentTurnSubmission submission) => Submissions.Add(submission);

		public void PrefillPrompt(string prompt) { }

		public void Interrupt() => Interruptions++;

		public void Restart() => Restarts++;

		public void ResolvePermission(string requestId, string optionId) { }

		public void ResolveInput(
			string requestId,
			string action,
			IReadOnlyDictionary<string, IReadOnlyList<string>> answers) { }

		public void Authenticate(string methodId, IReadOnlyDictionary<string, IReadOnlyList<string>> answers) { }

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}
