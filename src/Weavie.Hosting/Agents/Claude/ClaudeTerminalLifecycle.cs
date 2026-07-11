using System.Text;
using Weavie.Core.Agents;
using Weavie.Core.Configuration;
using Weavie.Core.Hooks;
using Weavie.Core.Sessions;

namespace Weavie.Hosting.Agents.Claude;

/// <summary>Claude CLI launch, resume, startup recovery, and transcript lifecycle for one worktree.</summary>
public sealed class ClaudeTerminalLifecycle : ITerminalProcess {
	private readonly SettingsStore _settings;
	private readonly ClaudeSessionStore _sessions;
	private readonly IClaudeTranscripts _transcripts;
	private readonly ClaudeLaunchConfiguration _configuration;
	private readonly Lock _gate = new();
	private ClaudeStartupWatcher? _startupWatcher;

	/// <summary>Creates the Claude terminal lifecycle rooted at <paramref name="workspace"/>.</summary>
	public ClaudeTerminalLifecycle(
		SettingsStore settings,
		string workspace,
		ClaudeSessionStore sessions,
		IClaudeTranscripts transcripts,
		ClaudeLaunchConfiguration configuration) {
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentException.ThrowIfNullOrEmpty(workspace);
		ArgumentNullException.ThrowIfNull(sessions);
		ArgumentNullException.ThrowIfNull(transcripts);
		ArgumentNullException.ThrowIfNull(configuration);
		_settings = settings;
		Workspace = workspace;
		_sessions = sessions;
		_transcripts = transcripts;
		_configuration = configuration;
	}

	/// <summary>The worktree Claude runs in.</summary>
	public string Workspace { get; }

	/// <inheritdoc/>
	public AgentLaunch ResolveLaunch() {
		var args = new List<string>();
		AddFileArgument(args, "--mcp-config", _configuration.McpConfigPath);
		AddFileArgument(args, "--settings", _configuration.SettingsFilePath);
		AddFileArgument(args, "--append-system-prompt-file", _configuration.SystemPromptFilePath);
		var managed = ResolveConversationLaunch();
		lock (_gate) {
			_startupWatcher = managed is not null ? new ClaudeStartupWatcher() : null;
		}
		if (managed is { } conversation) {
			args.Add(conversation.Resume ? "--resume" : "--session-id");
			args.Add(conversation.SessionId);
		}

		string? logPath = Environment.GetEnvironmentVariable("WEAVIE_PTY_LOG");
		return new AgentLaunch {
			Command = _settings.GetString("claude.path") ?? "claude",
			Arguments = args,
			WorkingDirectory = Workspace,
			RemoveEnvironment = ["ANTHROPIC_API_KEY"],
			Environment = _configuration.Environment,
			ExecutableMode = AgentExecutableMode.LoginShell,
			WorkingDirectoryMode = AgentWorkingDirectoryMode.Fixed,
			OutputCapture = string.IsNullOrEmpty(logPath)
				? new AgentOutputCapture.Disabled()
				: new AgentOutputCapture.File(logPath),
		};
	}

	/// <summary>Updates Claude conversation persistence from its hook stream.</summary>
	public void ObserveHook(HookRequest request) {
		ArgumentNullException.ThrowIfNull(request);
		if (!_settings.GetBool("claude.resumeSession", fallback: true)) {
			return;
		}
		switch (request.Event) {
			case HookEventKind.SessionStart when request.Source == "clear":
				_sessions.Clear(Workspace);
				break;
			case HookEventKind.UserPromptSubmit when !string.IsNullOrEmpty(request.SessionId):
				_sessions.Adopt(Workspace, request.SessionId);
				break;
			default:
				break;
		}
	}

	/// <inheritdoc/>
	public void ObserveTerminalOutput(ReadOnlyMemory<byte> data) {
		lock (_gate) {
			if (_startupWatcher is { Confirmed: false } watcher) {
				watcher.Observe(Encoding.UTF8.GetString(data.Span));
			}
		}
	}

	/// <inheritdoc/>
	public void ObserveTerminalInput(ReadOnlyMemory<byte> data) { }

	/// <inheritdoc/>
	public void ObserveProcessExit(AgentProcessExit exit) {
		ArgumentNullException.ThrowIfNull(exit);
		ClaudeStartupWatcher? watcher;
		lock (_gate) {
			watcher = _startupWatcher;
			_startupWatcher = null;
		}
		if (exit.Unexpected && watcher is not null && watcher.FailedToStart(exit.ExitCode)) {
			_sessions.Forget(Workspace);
		}
	}

	private ClaudeLaunch? ResolveConversationLaunch() {
		if (!_settings.GetBool("claude.resumeSession", fallback: true)) {
			return null;
		}
		string sessionId = _sessions.Resolve(Workspace);
		// Resume iff Claude already has a transcript for the id — the same check Claude enforces (see ClaudeSessionStore).
		return new ClaudeLaunch(sessionId, _transcripts.Exists(Workspace, sessionId));
	}

	private static void AddFileArgument(List<string> args, string flag, string path) {
		if (path.Length > 0) {
			args.Add(flag);
			args.Add(path);
		}
	}
}

/// <summary>The generated files and discovery environment required for a Claude CLI launch.</summary>
public sealed record ClaudeLaunchConfiguration {
	/// <summary>Claude IDE and hook discovery environment.</summary>
	public required IReadOnlyDictionary<string, string> Environment { get; init; }

	/// <summary>The registry MCP configuration path, or an empty string when absent.</summary>
	public required string McpConfigPath { get; init; }

	/// <summary>The Claude hook settings path, or an empty string when absent.</summary>
	public required string SettingsFilePath { get; init; }

	/// <summary>The Claude system-prompt appendix path, or an empty string when absent.</summary>
	public required string SystemPromptFilePath { get; init; }
}
