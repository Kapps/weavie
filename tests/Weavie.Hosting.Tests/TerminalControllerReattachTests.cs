using System.Text;
using System.Text.Json;
using Weavie.Core.Configuration;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// Drives a real <see cref="TerminalController"/> over a scriptable PTY to pin the reattach contract: an xterm
/// that mounts onto an already-live child gets a restore preamble carrying the child's latched terminal modes
/// (alt screen, mouse tracking, bracketed paste…) <em>before</em> the redraw nudge — without it, a fullscreen
/// TUI that entered the alt screen at startup renders into the fresh client's normal buffer forever (the
/// claude-pane phantom-scrollbar bug). A first start and a relaunched child must get no preamble at all. It also
/// pins the restored shell child's pre-spawn size: a size seeded before start (as <c>CreateSession</c> does from
/// the persisted terminal size) must be the spawn size, not the placeholder 80×24 — otherwise the shell's raw
/// scrollback replays at the wrong width and stacks garbled (the resume-console-scroll bug).
/// </summary>
public sealed class TerminalControllerReattachTests {
	private const string ClaudeStartupModes = "\x1b[?1049h\x1b[?25l\x1b[?1000h\x1b[?2004h";

	[Fact]
	public void FirstStart_PostsNoRestorePreamble() {
		using var h = new Harness();

		h.Controller.OnReady(80, 24);

		Assert.Empty(h.Bridge.PostedOfType("term-output"));
		Assert.Empty(h.Launcher.LastTerminal!.Resizes); // fresh launch is sized at spawn, not nudged
	}

	[Fact]
	public void ReattachToLiveChild_ReplaysLatchedModes_ThenNudges() {
		using var h = new Harness();
		h.Controller.OnReady(80, 24);
		h.Launcher.LastTerminal!.EmitOutput(Encoding.UTF8.GetBytes(ClaudeStartupModes));
		h.Bridge.Clear();

		h.Controller.OnReady(120, 40); // a second client mounts (reload / background session first viewed)

		var outputs = h.Bridge.PostedOfType("term-output");
		var posted = Assert.Single(outputs);
		Assert.Equal(ClaudeStartupModes, DecodeData(posted));
		Assert.Equal([(120, 39), (120, 40)], h.Launcher.LastTerminal!.Resizes);
	}

	[Fact]
	public void ReattachAfterRelaunch_DoesNotLeakThePreviousChildsModes() {
		using var h = new Harness();
		h.Controller.OnReady(80, 24);
		h.Launcher.LastTerminal!.EmitOutput(Encoding.UTF8.GetBytes(ClaudeStartupModes));

		// The child is relaunched (Restart tears down without auto-restart; the page's term-ready starts fresh).
		h.Controller.Restart();
		h.Controller.OnReady(80, 24);
		var relaunched = h.Launcher.LastTerminal!;
		h.Bridge.Clear();

		// A reattach to the NEW child (which set nothing yet) must replay nothing from the old one.
		h.Controller.OnReady(80, 24);

		Assert.Empty(h.Bridge.PostedOfType("term-output"));
		Assert.Equal([(80, 23), (80, 24)], relaunched.Resizes);
	}

	[Fact]
	public void RestoredShellChild_SpawnsAtSeededSize_NotThePlaceholder() {
		using var h = new Harness("shell");
		h.Controller.Resize(200, 50); // CreateSession seeds from the persisted size, before any pane mounts

		h.Controller.EnsureStarted(); // background pre-spawn (RestoreSessionState), before the page's term-ready

		Assert.Equal(200, h.Launcher.LastTerminal!.LastStartInfo!.Columns);
		Assert.Equal(50, h.Launcher.LastTerminal!.LastStartInfo!.Rows);
	}

	[Fact]
	public void RestoredShellChild_WithoutSeed_PreSpawnsAtThePlaceholder() {
		using var h = new Harness("shell");

		h.Controller.EnsureStarted();

		Assert.Equal(80, h.Launcher.LastTerminal!.LastStartInfo!.Columns);
		Assert.Equal(24, h.Launcher.LastTerminal!.LastStartInfo!.Rows);
	}

	private static string DecodeData(JsonElement message) =>
		Encoding.UTF8.GetString(Convert.FromBase64String(message.GetProperty("dataB64").GetString()!));

	/// <summary>A self-contained claude-pane controller over a scriptable PTY, torn down on dispose.</summary>
	private sealed class Harness : IDisposable {
		private readonly SettingsStore _settings;
		private readonly string _settingsPath;

		public Harness() : this("claude") { }

		public Harness(string session) {
			_settingsPath = Path.Combine(Path.GetTempPath(), "weavie-reattach-" + Guid.NewGuid().ToString("n") + ".toml");
			_settings = CoreSettings.CreateStore(_settingsPath, enableWatcher: false);
			Bridge = new FakeHostBridge();
			Launcher = new ScriptablePtyLauncher();
			Controller = new TerminalController(Bridge, session, _settings, Launcher) {
				Workspace = Path.GetTempPath(),
			};
		}

		public FakeHostBridge Bridge { get; }
		public ScriptablePtyLauncher Launcher { get; }
		public TerminalController Controller { get; }

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
