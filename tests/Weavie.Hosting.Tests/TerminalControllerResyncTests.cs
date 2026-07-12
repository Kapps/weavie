using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Configuration;
using Weavie.Core.Terminal;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// <see cref="TerminalController.ResyncPane"/> is the bridge-reconnect recovery: output posted while the link
/// was down never reached the page, so the shell (scrollback-backed) pane must be reset — its
/// <c>term-ready</c> reply replays the log — while the claude pane (no log) gets the size nudge that makes the
/// running TUI repaint. These pin which pane gets which, and that a never-started pane is left alone.
/// </summary>
public sealed class TerminalControllerResyncTests {
	[Fact]
	public void BeforeFirstStart_DoesNothing() {
		using var h = new Harness("claude", withScrollback: false);

		h.Controller.ResyncPane();

		Assert.Empty(h.Bridge.Posted);
		Assert.Null(h.Launcher.Terminal);
	}

	[Fact]
	public void PaneWithoutScrollback_NudgesThePtySizeToRepaint() {
		using var h = new Harness("claude", withScrollback: false);
		h.Controller.OnReady(80, 24);

		h.Controller.ResyncPane();

		Assert.Equal([(80, 23), (80, 24)], h.Launcher.Terminal!.Resizes);
		Assert.Empty(h.Bridge.PostedOfType("term-reset"));
	}

	[Fact]
	public void ShellOutputDuringResync_ReachesThePageExactlyOnce_ViaTheReplay() {
		using var h = new Harness("shell", withScrollback: true);
		h.Controller.OnReady(80, 24);

		// Output emitted between the reset and the page's term-ready reply must not post live: the page already
		// cleared the pane, and the replay (snapshotted below) contains it — posting both would paint it twice.
		h.Controller.ResyncPane();
		h.Launcher.Terminal!.EmitOutput("during-resync"u8.ToArray());
		Assert.Empty(h.Bridge.PostedOfType("term-output"));

		h.Controller.OnReady(80, 24); // the page's term-ready reply
		var replay = Assert.Single(h.Bridge.PostedOfType("term-output"));
		Assert.Contains("during-resync", DecodeData(replay));

		h.Launcher.Terminal.EmitOutput("after-resync"u8.ToArray()); // live delivery resumes
		Assert.Equal(2, h.Bridge.PostedOfType("term-output").Count);
	}

	[Fact]
	public void ReplayedScrollback_IsFlaggedReplay_LiveOutputIsNot() {
		using var h = new Harness("shell", withScrollback: true);
		h.Controller.OnReady(80, 24);
		// A device query the child once sent (answered by the then-live client) lands in the scrollback log.
		h.Launcher.Terminal!.EmitOutput("\x1b[6n"u8.ToArray());

		h.Controller.OnReady(80, 24); // reattach: the log replays the query bytes

		var outputs = h.Bridge.PostedOfType("term-output");
		Assert.Equal(2, outputs.Count);
		// Live output carries no flag; the replayed chunk is flagged so the page suppresses xterm's re-answer
		// (which would otherwise hit the child as input).
		Assert.False(outputs[0].TryGetProperty("replay", out _));
		Assert.True(outputs[1].GetProperty("replay").GetBoolean());
		Assert.Contains("\x1b[6n", DecodeData(outputs[1]));
	}

	[Fact]
	public void PaneWithScrollback_ResetsThePaneWithoutRespawn() {
		using var h = new Harness("shell", withScrollback: true);
		h.Controller.OnReady(80, 24);

		h.Controller.ResyncPane();

		var reset = Assert.Single(h.Bridge.PostedOfType("term-reset"));
		Assert.False(reset.GetProperty("respawn").GetBoolean()); // the child kept running: clear, don't reset modes
		Assert.Equal("shell", reset.GetProperty("session").GetString());
		Assert.Empty(h.Launcher.Terminal!.Resizes); // the replay+nudge comes from the page's term-ready reply
	}

	private static string DecodeData(System.Text.Json.JsonElement message) =>
		System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(message.GetProperty("dataB64").GetString()!));

	/// <summary>A real controller over a resize-recording PTY launcher; temp files torn down on dispose.</summary>
	private sealed class Harness : IDisposable {
		private readonly SettingsStore _settings;
		private readonly string _root;

		public Harness(string session, bool withScrollback) {
			_root = Directory.CreateDirectory(
				Path.Combine(Path.GetTempPath(), "weavie-resync-" + Guid.NewGuid().ToString("n"))).FullName;
			_settings = CoreSettings.CreateStore(Path.Combine(_root, "settings.toml"), enableWatcher: false);
			// Post output inline (no batching) so each emitted chunk is its own frame for these synchronous assertions.
			_settings.Set("terminal.outputCoalesceMs", JsonSerializer.SerializeToElement(0L));
			Launcher = new RecordingPtyLauncher();
			Controller = new TerminalController(
				Bridge,
				session,
				_settings,
				Launcher,
				new TestTerminalProcess(_root, AgentWorkingDirectoryMode.Fixed)) {
				Workspace = _root,
			};
			if (withScrollback) {
				Controller.ScrollbackLogPath = Path.Combine(_root, "scrollback.log");
			}
		}

		public FakeHostBridge Bridge { get; } = new();
		public RecordingPtyLauncher Launcher { get; }
		public TerminalController Controller { get; }

		public void Dispose() {
			Controller.Dispose();
			_settings.Dispose();
			try {
				Directory.Delete(_root, recursive: true);
			} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
				// best-effort temp cleanup
			}
		}
	}

	/// <summary>An <see cref="IPtyLauncher"/> whose terminal records every <c>Resize</c> it receives.</summary>
	private sealed class RecordingPtyLauncher : IPtyLauncher {
		/// <summary>The most recently created terminal, or null before the first launch.</summary>
		public RecordingTerminal? Terminal { get; private set; }

		public ITerminal CreateTerminal() => Terminal = new RecordingTerminal();

		public PtyLaunch Resolve(AgentLaunch launch) => new() {
			Command = launch.Command,
			Arguments = launch.Arguments,
			RemoveEnvironment = launch.RemoveEnvironment,
			Environment = launch.Environment,
		};
	}

	/// <summary>An <see cref="ITerminal"/> that never spawns a child and records the sizes it is resized to.</summary>
	private sealed class RecordingTerminal : ITerminal {
		public event Action<byte[]>? Output;
		public event Action<int>? Exited;

		public bool IsRunning { get; private set; }

		public bool HasForegroundJob => false;

		/// <summary>Every (columns, rows) passed to <see cref="Resize"/>, in order.</summary>
		public List<(int Columns, int Rows)> Resizes { get; } = [];

		public void Start(TerminalStartInfo startInfo) {
			IsRunning = true;
			_ = Exited;
		}

		/// <summary>Raises <see cref="Output"/> as if the child emitted <paramref name="data"/>.</summary>
		public void EmitOutput(byte[] data) => Output?.Invoke(data);

		public void Write(byte[] data) {
			// no child to write to
		}

		public void Resize(int columns, int rows) => Resizes.Add((columns, rows));

		public void Dispose() => IsRunning = false;
	}
}
