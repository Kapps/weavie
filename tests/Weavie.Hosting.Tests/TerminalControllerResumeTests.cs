using Weavie.Core.Agents;
using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;
using Weavie.Core.Hooks;
using Weavie.Core.Sessions;
using Weavie.Hosting.Agents.Claude;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// Drives a real <see cref="TerminalController"/> (claude pane) over a scriptable PTY to pin down the
/// session-resume wiring end-to-end: which launch flag it passes (<c>--resume</c> vs <c>--session-id</c>) and
/// what decides it. The invariant under test: <b>the transcript on disk is the single source of truth</b> — a
/// launch resumes exactly when Claude already has a transcript for the id (so an id whose conversation exists is
/// never re-created → "Session ID … is already in use"), and re-creates fresh when it does not (so a never-yet-
/// messaged or pruned id never dead-panes with "No conversation found"). An unconfirmed startup crash forgets
/// the id so the next launch mints a fresh one.
/// </summary>
public sealed class TerminalControllerResumeTests {
	[Fact]
	public void NoTranscript_CreatesWithSessionId() {
		using var h = new Harness();

		h.Controller.OnReady(80, 24); // fresh session, no transcript → create with --session-id

		Assert.Equal(["--session-id", h.SessionId], h.Launcher.LastClaudeSessionArguments);
	}

	[Fact]
	public void TranscriptExists_ResumesEvenWithoutAdopt() {
		// The reported "Session ID … is already in use" bug: the store tracks an id whose transcript still exists on
		// disk, but the id was never re-adopted (a resume that failed for a transient reason had cleared the old
		// "started" bit). The launch must --resume the existing conversation, not re-create it under --session-id.
		using var h = new Harness();
		string id = h.SessionId;
		h.CreateTranscript(id); // Claude has the conversation, but nothing was adopted this run

		h.Controller.OnReady(80, 24);

		Assert.Equal(["--resume", id], h.Launcher.LastClaudeSessionArguments);
	}

	[Fact]
	public void PaintingTheTui_WithoutATranscript_DoesNotResume() {
		// Painting a TUI is not a conversation: an id that came up but was never messaged has no transcript, so the
		// next launch must re-create it, not --resume a doomed id (the "missing session" bug).
		using var h = new Harness();

		h.Controller.OnReady(80, 24);
		h.Launcher.LastTerminal!.EmitOutput(new byte[8192]); // a full TUI's worth of output, still no transcript

		Assert.Equal(["--session-id", h.SessionId], h.NextLaunchArgs()); // a relaunch still re-creates
	}

	[Fact]
	public void TranscriptMissing_EvenAfterAdopt_CreatesFresh() {
		// A prior run adopted the id, but Claude no longer has its transcript under this cwd (cleared, or filed under
		// a different directory). The launch must NOT --resume a doomed id — it re-creates under --session-id.
		using var h = new Harness();
		string id = h.SessionId;
		h.Store.Adopt(h.Workspace, id); // adopted, but no transcript written

		h.Controller.OnReady(80, 24);

		Assert.Equal(["--session-id", id], h.Launcher.LastClaudeSessionArguments);
	}

	[Fact]
	public void UnconfirmedStartupCrash_ForgetsId_SoNextLaunchMintsFresh() {
		using var h = new Harness();
		string id = h.SessionId;
		h.CreateTranscript(id);
		h.Controller.OnReady(80, 24); // --resume <id>, its startup unconfirmed

		// The resume dies at startup without ever painting (a short error, well under the confirm threshold).
		h.Agent.ObserveProcessExit(new AgentProcessExit { ExitCode = 1, Unexpected = true });

		Assert.NotEqual(id, h.SessionId); // the id was forgotten; the next launch mints a fresh one
	}

	[Fact]
	public void ConfirmedThenCrash_KeepsId() {
		using var h = new Harness();
		string id = h.SessionId;
		h.CreateTranscript(id);
		h.Controller.OnReady(80, 24);
		h.Launcher.LastTerminal!.EmitOutput(new byte[8192]); // came up (confirmed), then later crashed

		h.Agent.ObserveProcessExit(new AgentProcessExit { ExitCode = 1, Unexpected = true });

		Assert.Equal(id, h.SessionId); // a crash after coming up is not a startup failure — the id stands
	}

	[Fact]
	public void ClearHook_AbandonsTheTrackedSession() {
		using var h = new Harness();
		string id = h.SessionId;
		h.Store.Adopt(h.Workspace, id);

		// A /clear surfaces as a SessionStart hook sourced "clear": it abandons the tracked id so the next launch
		// cold-starts on a fresh id (only a SessionStart from /clear does this — a different source must not).
		h.Agent.ObserveHook(new HookRequest {
			Event = HookEventKind.SessionStart,
			Source = "clear",
			ToolName = string.Empty,
			ToolInputJson = "{}",
		});

		Assert.NotEqual(id, h.SessionId);
	}

	/// <summary>A self-contained controller + scriptable PTY + isolated stores over an in-memory transcript tree.</summary>
	private sealed class Harness : IDisposable {
		private readonly SettingsStore _settings;
		private readonly string _settingsPath;
		private readonly InMemoryFileSystem _fs = new();
		private readonly ClaudeTranscripts _transcripts;

		public Harness() {
			_settingsPath = Path.Combine(Path.GetTempPath(), "weavie-tc-" + Guid.NewGuid().ToString("n") + ".toml");
			_settings = CoreSettings.CreateStore(_settingsPath, enableWatcher: false);
			Bridge = new FakeHostBridge();
			Store = new ClaudeSessionStore(new InMemoryFileSystem(), "/weavie-tc/claude-sessions.json");
			_transcripts = new ClaudeTranscripts(_fs, "/claude/projects");
			Launcher = new ScriptablePtyLauncher();
			Workspace = Path.Combine(Path.GetTempPath(), "weavie-tc-ws-" + Guid.NewGuid().ToString("n"));
			Agent = new ClaudeTerminalLifecycle(
				_settings,
				Workspace,
				Store,
				_transcripts,
				new ClaudeLaunchConfiguration {
					Environment = new Dictionary<string, string>(StringComparer.Ordinal),
					McpConfigPath = string.Empty,
					SettingsFilePath = string.Empty,
					SystemPromptFilePath = string.Empty,
				});
			Controller = new TerminalController(Bridge, "claude", _settings, Launcher, Agent) {
				Workspace = Workspace,
			};
		}

		public FakeHostBridge Bridge { get; }
		public ClaudeSessionStore Store { get; }
		public ScriptablePtyLauncher Launcher { get; }
		public ClaudeTerminalLifecycle Agent { get; }
		public TerminalController Controller { get; }
		public string Workspace { get; }

		/// <summary>The stable id assigned to this workspace (minting it if needed — same id the controller uses).</summary>
		public string SessionId => Store.Resolve(Workspace);

		/// <summary>Writes a Claude transcript for <paramref name="id"/> under this workspace, so a launch resumes it.</summary>
		public void CreateTranscript(string id) => _fs.WriteAllText(_transcripts.TranscriptPath(Workspace, id), "{}");

		/// <summary>The <c>--resume</c>/<c>--session-id</c> pair the next launch would pass, resolved fresh.</summary>
		public IReadOnlyList<string> NextLaunchArgs() {
			var args = Agent.ResolveLaunch().Arguments;
			int index = args.ToList().FindIndex(arg => arg is "--resume" or "--session-id");
			return index < 0 ? [] : args.Skip(index).Take(2).ToArray();
		}

		public void Dispose() {
			Controller.Dispose();
			_settings.Dispose();
			try {
				File.Delete(_settingsPath);
			} catch (IOException) {
				// best-effort temp cleanup
			}
		}
	}
}
