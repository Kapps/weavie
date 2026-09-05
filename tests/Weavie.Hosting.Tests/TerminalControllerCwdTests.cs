using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Configuration;
using Weavie.Core.Terminal;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// OSC 7 cwd is untrusted terminal output that becomes the relaunched shell's working directory, so it must be
/// confined to the session's worktree. These pin the boundary end-to-end — driving a real
/// <see cref="TerminalController"/> and asserting the directory the next launch *actually* uses — so the guard
/// can't be silently dropped (the path-injection class CodeQL flagged).
/// </summary>
public sealed class TerminalControllerCwdTests {
	[Fact]
	public void InWorkspaceCwd_BecomesTheRelaunchDirectory() {
		using var h = new Harness();
		string nested = Directory.CreateDirectory(Path.Combine(h.Workspace, "src", "nested")).FullName;

		h.Controller.OnReady(80, 24);
		Assert.Equal(h.Workspace, h.Launcher.LastWorkingDirectory); // first launch: nothing reported yet

		h.Controller.OnCwdReported(nested);
		h.Relaunch();

		Assert.Equal(nested, h.Launcher.LastWorkingDirectory);
	}

	[Fact]
	public void ExistingSiblingOutsideTheWorkspace_IsIgnored() {
		using var h = new Harness();
		// A directory that genuinely EXISTS (so Directory.Exists passes) but is OUTSIDE the worktree, and whose
		// path has the worktree as a string prefix — isolating the containment guard from the existence check.
		string evil = Directory.CreateDirectory(h.Workspace + "-evil").FullName;

		h.Controller.OnReady(80, 24);
		h.Controller.OnCwdReported(evil);
		h.Relaunch();

		Assert.Equal(h.Workspace, h.Launcher.LastWorkingDirectory); // rejected → fell back to the workspace root
	}

	[Fact]
	public void TraversalToAnExistingDirectoryOutside_IsIgnored() {
		using var h = new Harness();
		string evil = Directory.CreateDirectory(h.Workspace + "-evil").FullName;
		// A traversal string that resolves to the existing outside directory — proves containment (not existence)
		// is what rejects it.
		string traversal = Path.Combine(h.Workspace, "..", Path.GetFileName(evil));

		h.Controller.OnReady(80, 24);
		h.Controller.OnCwdReported(traversal);
		h.Relaunch();

		Assert.Equal(h.Workspace, h.Launcher.LastWorkingDirectory);
	}

	/// <summary>A real shell-pane controller over a recording PTY launcher; temp worktree torn down on dispose.</summary>
	private sealed class Harness : IDisposable {
		private readonly SettingsStore _settings;
		private readonly TempDirectory _temp = new("weavie-tcwd");

		public Harness() {
			_settings = CoreSettings.CreateStore(_temp.Combine("settings.toml"), enableWatcher: false);
			// Post output inline (no batching) so each emitted chunk is its own frame for these synchronous assertions.
			_settings.Set("terminal.outputCoalesceMs", JsonSerializer.SerializeToElement(0L));
			// A sibling "<name>-evil" lives beside it inside the same temp root; both go on dispose.
			Workspace = _temp.CreateDirectory("ws");
			Launcher = new RecordingPtyLauncher();
			var bridge = new FakeHostBridge();
			Controller = new TerminalController(
				bridge.SessionFeature("terminal.shell"),
				"shell",
				_settings,
				Launcher,
				new TestTerminalProcess(Workspace, AgentWorkingDirectoryMode.FollowReported)) {
				Workspace = Workspace,
			};
		}

		public RecordingPtyLauncher Launcher { get; }
		public TerminalController Controller { get; }
		public string Workspace { get; }

		/// <summary>Tears the child down and brings it back up — the synchronous Reopen-Terminal path.</summary>
		public void Relaunch() {
			Controller.Restart();
			Controller.OnReady(80, 24);
		}

		public void Dispose() {
			Controller.Dispose();
			_settings.Dispose();
			_temp.Dispose();
		}
	}

	/// <summary>An <see cref="IPtyLauncher"/> whose terminal records the working directory each launch is started with.</summary>
	private sealed class RecordingPtyLauncher : IPtyLauncher {
		private RecordingTerminal? _last;

		/// <summary>The working directory the most recent launch was started with.</summary>
		public string? LastWorkingDirectory => _last?.WorkingDirectory;

		public ITerminal CreateTerminal() => _last = new RecordingTerminal();

		public PtyLaunch Resolve(AgentLaunch launch) => new() {
			Command = launch.Command,
			Arguments = launch.Arguments,
			RemoveEnvironment = launch.RemoveEnvironment,
			Environment = launch.Environment,
		};
	}

	/// <summary>An <see cref="ITerminal"/> that never spawns a child but remembers the working directory it was started with.</summary>
	private sealed class RecordingTerminal : ITerminal {
		public event Action<byte[]>? Output;
		public event Action<int>? Exited;

		public bool IsRunning { get; private set; }
		public bool HasForegroundJob => false;
		public string? WorkingDirectory { get; private set; }

		public void Start(TerminalStartInfo startInfo) {
			IsRunning = true;
			WorkingDirectory = startInfo.WorkingDirectory;
			_ = Output;
			_ = Exited;
		}

		public void Write(byte[] data) {
			// no child to write to
		}

		public void Resize(int columns, int rows) {
			// no PTY to resize
		}

		public void Dispose() => IsRunning = false;
	}
}
