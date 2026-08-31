using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Corrections;
using Weavie.Core.FileSystem;
using Weavie.Core.Layout;
using Weavie.Core.Mcp;
using Weavie.Core.Sessions;
using Weavie.Core.Theming;
using Weavie.Hosting.Agents.Claude;
using Weavie.Hosting.Messaging;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// Pins how a session's first input reaches a terminal-backed agent: as part of the agent's launch, never as
/// keystrokes typed at it. A TUI that is still starting discards written input outright, and once it is up it
/// reads a burst of raw input as a paste — so the submit key riding that burst is absorbed as text and the turn
/// is never sent. Either way the prompt silently disappears, which is what injection cost us.
/// </summary>
public sealed class InitialTerminalInputTests : IDisposable {
	private readonly string _dir = Path.Combine(
		Path.GetTempPath(), "weavie-initial-terminal-input", Guid.NewGuid().ToString("N"));
	private readonly ScriptablePtyLauncher _launcher = new();

	public InitialTerminalInputTests() {
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
	public void FirstTurn_IsClaudesOpeningPromptArgument() {
		using var lifecycle = new ClaudeLifecycleHarness();
		lifecycle.Agent.SeedFirstTurn(Input("build the thing"));

		var arguments = lifecycle.Agent.ResolveLaunch().Arguments;

		Assert.Equal("build the thing", arguments[^1]);
	}

	[Fact]
	public void FirstTurn_LeadsWithImagePaths_WhichClaudeAttachesFromTheirPath() {
		using var lifecycle = new ClaudeLifecycleHarness();
		lifecycle.Agent.SeedFirstTurn(Input("what is this", new AgentInputAttachment {
			Id = "image-1",
			Mime = "image/png",
			Path = "/shots/one.png",
		}));

		var arguments = lifecycle.Agent.ResolveLaunch().Arguments;

		Assert.Equal("/shots/one.png\nwhat is this", arguments[^1]);
	}

	[Fact]
	public void FirstTurn_IsConsumedByOneLaunch_SoARestartNeverResubmitsIt() {
		using var lifecycle = new ClaudeLifecycleHarness();
		lifecycle.Agent.SeedFirstTurn(Input("build the thing"));
		lifecycle.Agent.ResolveLaunch();

		var relaunch = lifecycle.Agent.ResolveLaunch().Arguments;

		Assert.DoesNotContain("build the thing", relaunch);
	}

	[Fact]
	public void SeedFirstTurn_AfterTheAgentLaunched_Throws() {
		using var lifecycle = new ClaudeLifecycleHarness();
		lifecycle.Agent.ResolveLaunch();

		Assert.Throws<InvalidOperationException>(() => lifecycle.Agent.SeedFirstTurn(Input("too late")));
	}

	[Fact]
	public async Task InitialInput_ReachesTheLaunch_AndIsNeverTypedAtThePane() {
		using var settings = CoreSettings.CreateStore(Path.Combine(_dir, "settings.toml"), enableWatcher: false);
		var provider = new FakeTerminalAgentProvider(_dir);
		await using var session = CreateSession(settings, provider);

		session.QueueInitialInput(Input("build the thing"));
		session.Claude!.OnReady(80, 24);
		session.Status.Observe(new AgentSessionStarted("startup"));

		Assert.Equal("build the thing", provider.Session!.FirstTurn?.Text);
		Assert.Empty(_launcher.LastTerminal!.Writes);
	}

	private static AgentTurnSubmission Input(string text, params AgentInputAttachment[] attachments) =>
		new() {
			Id = "initial",
			Text = text,
			Attachments = attachments,
		};

	private HostSession CreateSession(SettingsStore settings, IAgentProvider provider) {
		var commandRegistry = CoreCommands.CreateRegistry();
		var endpoint = new HostMessageRouter(
			new FakeHostBridge(),
			new InlineUiDispatcher(),
			_ => { }).OpenSession(new SessionAddress("slot-1", Guid.NewGuid().ToString("n")));
		var session = new HostSession(
			endpoint,
			settings,
			new LayoutStore(new LocalFileSystem(), LayoutPanes.CreateRegistry(), Path.Combine(_dir, "layout.json")),
			_dir,
			Path.Combine(_dir, "scratch"),
			Path.Combine(_dir, "pasted"),
			Path.Combine(_dir, "agent-pane.json"),
			[ShellTerminalId.New()],
			id => Path.Combine(_dir, $"shell-{id}.json"),
			commandRegistry,
			new KeybindingStore(commandRegistry, Path.Combine(_dir, "keybindings.json"), enableWatcher: false),
			new ThemeOverridesStore(new LocalFileSystem(), Path.Combine(_dir, "theme-overrides.json")),
			new CorrectionCorpus(new LocalFileSystem(), Path.Combine(_dir, "corrections.jsonl")),
			UnusedInferenceService.Instance,
			_launcher,
			provider,
			new HostRuntimeInfo(HostTransport.Local, Managed: false, "test"),
			() => false,
			(_, accept) => accept(),
			(_, _) => { });
		session.ActivateOwnedRuntimeAndMessages();
		return session;
	}

	/// <summary>The real Claude lifecycle over isolated in-memory stores, for launch-argument assertions.</summary>
	private sealed class ClaudeLifecycleHarness : IDisposable {
		private readonly SettingsStore _settings;
		private readonly string _settingsPath;

		public ClaudeLifecycleHarness() {
			_settingsPath = Path.Combine(Path.GetTempPath(), "weavie-first-turn-" + Guid.NewGuid().ToString("n") + ".toml");
			_settings = CoreSettings.CreateStore(_settingsPath, enableWatcher: false);
			_settings.Set(CoreSettings.ClaudePath, JsonSerializer.SerializeToElement("claude"));
			Agent = new ClaudeTerminalLifecycle(
				_settings,
				Path.Combine(Path.GetTempPath(), "weavie-first-turn-ws-" + Guid.NewGuid().ToString("n")),
				new ClaudeSessionStore(new InMemoryFileSystem(), "/weavie/claude-sessions.json"),
				new ClaudeTranscripts(new InMemoryFileSystem(), "/claude/projects"),
				new ClaudeLaunchConfiguration {
					Environment = new Dictionary<string, string>(StringComparer.Ordinal),
					McpConfigPath = string.Empty,
					SettingsFilePath = string.Empty,
					SystemPromptFilePath = string.Empty,
				});
		}

		public ClaudeTerminalLifecycle Agent { get; }

		public void Dispose() {
			_settings.Dispose();
			try {
				File.Delete(_settingsPath);
			} catch (IOException) {
				// best-effort temp cleanup
			}
		}
	}

	private sealed class FakeTerminalAgentProvider(string workspace) : IAgentProvider {
		public AgentProviderInfo Info { get; } = new() {
			Id = "terminal",
			Name = "Fake terminal agent",
			Capabilities = AgentProviderCapabilities.Terminal,
			Available = true,
		};

		public FakeTerminalAgentSession? Session { get; private set; }

		public IAgentSession CreateSession(AgentSessionContext context) => Session = new FakeTerminalAgentSession(workspace);

		internal sealed class FakeTerminalAgentSession(string workspace) : ITerminalAgentSession {
			public AgentTurnSubmission? FirstTurn { get; private set; }

			public void SeedFirstTurn(AgentTurnSubmission turn) => FirstTurn = turn;

			public AgentLaunch ResolveLaunch() => new() {
				Command = "noop",
				Arguments = FirstTurn is { } turn ? [turn.Text] : [],
				WorkingDirectory = workspace,
				RemoveEnvironment = [],
				Environment = new Dictionary<string, string>(StringComparer.Ordinal),
				ExecutableMode = AgentExecutableMode.Direct,
				WorkingDirectoryMode = AgentWorkingDirectoryMode.Fixed,
				OutputCapture = new AgentOutputCapture.Disabled(),
			};

			public void ObserveTerminalOutput(ReadOnlyMemory<byte> data) { }

			public void ObserveTerminalInput(ReadOnlyMemory<byte> data) { }

			public void ObserveProcessExit(AgentProcessExit exit) { }

			public ValueTask DisposeAsync() => ValueTask.CompletedTask;
		}
	}
}
