using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;
using Weavie.Core.Hooks;
using Weavie.Core.Sessions;
using Weavie.Core.Terminal;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// Drives a real <see cref="TerminalController"/> (claude pane) over a scriptable PTY to pin down the
/// session-resume wiring end-to-end: which launch flag it passes (<c>--resume</c> vs <c>--session-id</c>) and
/// what flips a session to resumable. The invariant under test: <b>only a real user message</b> (a
/// <c>UserPromptSubmit</c> hook, adopted into <see cref="ClaudeSessionStore"/>) makes a session resumable —
/// claude painting its TUI never does (that was the "missing session" bug), and a session adopted in a prior
/// run stays resumable even when a later run resumes it without sending a new message.
/// </summary>
public sealed class TerminalControllerResumeTests {
	[Fact]
	public void PaintingTheTui_DoesNotMakeSessionResumable() {
		using var h = new Harness();

		h.Controller.OnReady(80, 24); // fresh session → create with --session-id
		Assert.Equal(["--session-id", h.SessionId], h.Launcher.LastClaudeSessionArguments);

		// Stream a full TUI's worth of output (well past the startup-confirm threshold) without any message.
		h.Launcher.LastTerminal!.EmitOutput(new byte[8192]);

		Assert.False(h.Resumable); // painting is not a conversation: the next launch must NOT --resume
	}

	[Fact]
	public void AdoptingAUserMessage_MakesSessionResumable() {
		using var h = new Harness();
		h.Controller.OnReady(80, 24);
		string id = h.SessionId;
		Assert.False(h.Resumable); // came up, but not messaged yet

		h.Controller.ObserveHook(new HookRequest {
			Event = HookEventKind.UserPromptSubmit,
			ToolName = string.Empty,
			ToolInputJson = "{}",
			SessionId = id,
		});

		Assert.True(h.Resumable); // the first user message is what marks it resumable
	}

	[Fact]
	public void ResumedWithoutANewMessage_LaunchesWithResume_AndStaysResumable() {
		using var h = new Harness();
		// A prior run sent a message, so the session is resumable.
		string id = h.SessionId;
		h.Store.Adopt(h.Workspace, id);
		Assert.True(h.Resumable);

		// This run resumes it...
		h.Controller.OnReady(80, 24);
		Assert.Equal(["--resume", id], h.Launcher.LastClaudeSessionArguments);

		// ...paints, but no new message is sent before unloading.
		h.Launcher.LastTerminal!.EmitOutput(new byte[8192]);

		Assert.True(h.Resumable); // still resumable next time — the started flag is durable
	}

	[Fact]
	public void ClearHook_AbandonsTheTrackedSession() {
		using var h = new Harness();
		string id = h.SessionId;
		h.Store.Adopt(h.Workspace, id); // a prior message made it resumable
		Assert.True(h.Resumable);

		// A /clear surfaces as a SessionStart hook sourced "clear": it abandons the tracked id so the next launch
		// cold-starts (only a SessionStart from /clear does this — a different source must not).
		h.Controller.ObserveHook(new HookRequest {
			Event = HookEventKind.SessionStart,
			Source = "clear",
			ToolName = string.Empty,
			ToolInputJson = "{}",
		});

		Assert.False(h.Resumable);
	}

	/// <summary>A self-contained controller + scriptable PTY + isolated stores, torn down on dispose.</summary>
	private sealed class Harness : IDisposable {
		private readonly SettingsStore _settings;
		private readonly string _settingsPath;

		public Harness() {
			_settingsPath = Path.Combine(Path.GetTempPath(), "weavie-tc-" + Guid.NewGuid().ToString("n") + ".toml");
			_settings = CoreSettings.CreateStore(_settingsPath, enableWatcher: false);
			Bridge = new FakeHostBridge();
			Store = new ClaudeSessionStore(new InMemoryFileSystem(), "/weavie-tc/claude-sessions.json");
			Launcher = new ScriptablePtyLauncher();
			Workspace = Path.Combine(Path.GetTempPath(), "weavie-tc-ws-" + Guid.NewGuid().ToString("n"));
			Controller = new TerminalController(Bridge, "claude", _settings, Launcher) {
				Workspace = Workspace,
				ClaudeSessions = Store,
			};
		}

		public FakeHostBridge Bridge { get; }
		public ClaudeSessionStore Store { get; }
		public ScriptablePtyLauncher Launcher { get; }
		public TerminalController Controller { get; }
		public string Workspace { get; }

		/// <summary>The stable id assigned to this workspace (minting it if needed — same id the controller uses).</summary>
		public string SessionId => Store.Resolve(Workspace).SessionId;

		/// <summary>Whether the next launch would <c>--resume</c> (true) or re-create with <c>--session-id</c> (false).</summary>
		public bool Resumable => Store.Resolve(Workspace).Resume;

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

	/// <summary>An <see cref="IPtyLauncher"/> that records the resolved session args and hands back a terminal the test can drive.</summary>
	private sealed class ScriptablePtyLauncher : IPtyLauncher {
		public IReadOnlyList<string> LastClaudeSessionArguments { get; private set; } = [];
		public ScriptableTerminal? LastTerminal { get; private set; }

		public ITerminal CreateTerminal() => LastTerminal = new ScriptableTerminal();

		public PtyLaunch Resolve(PtyLaunchRequest request) {
			LastClaudeSessionArguments = request.ClaudeSessionArguments;
			return new PtyLaunch {
				Command = "noop",
				Arguments = request.BuildClaudeArguments(),
				RemoveEnvironment = [],
				Environment = new Dictionary<string, string>(StringComparer.Ordinal),
			};
		}
	}

	/// <summary>An <see cref="ITerminal"/> that never spawns a child but lets the test raise its output/exit events.</summary>
	private sealed class ScriptableTerminal : ITerminal {
		public event Action<byte[]>? Output;
		public event Action<int>? Exited;

		public bool IsRunning { get; private set; }

		public void Start(TerminalStartInfo startInfo) {
			IsRunning = true;
			_ = Exited; // ITerminal requires the event; this fake never exits on its own (CS0067 suppression)
		}

		public void Write(byte[] data) {
			// no child to write to
		}

		public void Resize(int columns, int rows) {
			// no PTY to resize
		}

		public void Dispose() => IsRunning = false;

		/// <summary>Raises the <see cref="Output"/> event with <paramref name="data"/>, as a live child would.</summary>
		public void EmitOutput(byte[] data) => Output?.Invoke(data);
	}
}
