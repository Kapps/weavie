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
	private readonly TempDirectory _dir = new("weavie-initial-terminal-input");
	private readonly ScriptablePtyLauncher _launcher = new();

	public void Dispose() => _dir.Dispose();

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
		using var settings = CoreSettings.CreateStore(_dir.Combine("settings.toml"), enableWatcher: false);
		var provider = new FakeTerminalAgentProvider(_dir.Path);
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
			Kind = AgentTurnSubmissionKind.Prompt,
			CommandName = string.Empty,
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
			new LayoutStore(new LocalFileSystem(), LayoutPanes.CreateRegistry(), _dir.Combine("layout.json")),
			_dir.Path,
			_dir.Combine("scratch"),
			_dir.Combine("pasted"),
			_dir.Combine("agent-pane.json"),
			[ShellTerminalId.New()],
			id => _dir.Combine($"shell-{id}.json"),
			commandRegistry,
			new KeybindingStore(commandRegistry, _dir.Combine("keybindings.json"), enableWatcher: false),
			new ThemeOverridesStore(new LocalFileSystem(), _dir.Combine("theme-overrides.json")),
			new CorrectionCorpus(new LocalFileSystem(), _dir.Combine("corrections.jsonl")),
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
		private readonly TempDirectory _temp = new("weavie-first-turn");

		public ClaudeLifecycleHarness() {
			_settings = CoreSettings.CreateStore(_temp.Combine("settings.toml"), enableWatcher: false);
			_settings.Set(CoreSettings.ClaudePath, JsonSerializer.SerializeToElement("claude"));
			Agent = new ClaudeTerminalLifecycle(
				_settings,
				_temp.Combine("workspace"),
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
			_temp.Dispose();
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
