using System.Text;
using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Configuration;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// Pins the batching boundaries <see cref="TerminalController"/> gains when live output is coalesced (the
/// default): output is held rather than posted per PTY chunk, a reattach flushes the buffer ahead of the
/// mode-restore preamble, and a resync discards the buffer so the scrollback replay delivers those bytes exactly
/// once (no double-paint). A long window keeps the time-flush from firing, so each test drives its own drain
/// boundary — fully deterministic.
/// </summary>
public sealed class TerminalControllerCoalesceTests {
	private const string ClaudeStartupModes = "\x1b[?1049h\x1b[?25l\x1b[?1000h\x1b[?2004h";

	[Fact]
	public void BatchedOutput_IsHeldUntilAReattachFlushesIt_BeforeTheModePreamble() {
		using var h = new Harness("claude", withScrollback: false);
		h.Controller.OnReady(80, 24);
		h.Launcher.LastTerminal!.EmitOutput(Encoding.UTF8.GetBytes(ClaudeStartupModes));
		Assert.Empty(h.Bridge.PostedOfType("term-output")); // batched, not posted per-chunk
		h.Bridge.Clear();

		h.Controller.OnReady(120, 40); // a reattach: flush the buffer, then the restore preamble

		var outputs = h.Bridge.PostedOfType("term-output");
		Assert.Equal(2, outputs.Count);
		Assert.False(outputs[0].TryGetProperty("replay", out _)); // the flushed live output, first
		Assert.Equal(ClaudeStartupModes, DecodeData(outputs[0]));
		Assert.True(outputs[1].GetProperty("replay").GetBoolean()); // the synthesized preamble, after it
	}

	[Fact]
	public void BatchedOutput_DuringResync_ReachesThePageOnce_ViaTheReplay() {
		using var h = new Harness("shell", withScrollback: true);
		h.Controller.OnReady(80, 24);
		h.Launcher.LastTerminal!.EmitOutput(Encoding.UTF8.GetBytes("batched-line\r\n"));
		Assert.Empty(h.Bridge.PostedOfType("term-output")); // batched (and written to scrollback)
		h.Bridge.Clear();

		h.Controller.ResyncPane();    // discards the buffer; the coming replay carries the same bytes
		h.Controller.OnReady(80, 24); // the page's term-ready reply replays the log

		var replay = Assert.Single(h.Bridge.PostedOfType("term-output"));
		Assert.Contains("batched-line", DecodeData(replay));
		Assert.True(replay.GetProperty("replay").GetBoolean());
	}

	private static string DecodeData(JsonElement message) =>
		Encoding.UTF8.GetString(Convert.FromBase64String(message.GetProperty("dataB64").GetString()!));

	/// <summary>A controller with batching left on (a long window), so a test drives each drain boundary itself.</summary>
	private sealed class Harness : IDisposable {
		private const long LongWindowMs = 600_000;
		private readonly SettingsStore _settings;
		private readonly string _root;

		public Harness(string session, bool withScrollback) {
			_root = Directory.CreateDirectory(
				Path.Combine(Path.GetTempPath(), "weavie-coalesce-" + Guid.NewGuid().ToString("n"))).FullName;
			_settings = CoreSettings.CreateStore(Path.Combine(_root, "settings.toml"), enableWatcher: false);
			_settings.Set("terminal.outputCoalesceMs", JsonSerializer.SerializeToElement(LongWindowMs));
			Launcher = new ScriptablePtyLauncher();
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
		public ScriptablePtyLauncher Launcher { get; }
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
}
