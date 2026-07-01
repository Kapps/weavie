using System.Diagnostics;
using Weavie.Core;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Diagnostics;
using Weavie.Core.FileSystem;
using Weavie.Core.Remote;
using Weavie.Core.Sessions;
using Weavie.Core.Shell;
using Weavie.Core.Terminal;
using Weavie.Core.Theming;

namespace Weavie.Hosting.Tests;

/// <summary>
/// A real <see cref="HostCore"/> over a throwaway git repo, wired to a <see cref="FakeHostBridge"/> and no-op
/// PTYs, so tests can drive web messages end-to-end and assert on what the host posts back. This exercises the
/// genuine session-routing paths (fs by path, the editor-session owner guard, LSP rebind on switch) rather than
/// a reimplementation of them. Requires <c>git</c> on PATH, like <c>WorktreeIntegrationTests</c>. Stores are
/// isolated to a temp dir (no watchers, no real ~/.weavie config); the IDE's own port-scoped lock/internals
/// files still land in the real Weavie dirs and are cleaned on dispose (lock) or harmlessly overwritten.
/// </summary>
internal sealed class TestHost : IAsyncDisposable {
	private readonly string _tempRoot;
	private readonly HostServices _services;

	private TestHost(string tempRoot, string repoRoot, HostServices services, FakeHostBridge bridge, TestPlatform platform, HostCore core, StubHttpMessageHandler sourceHttp, string sourcesDir) {
		_tempRoot = tempRoot;
		RepoRoot = repoRoot;
		_services = services;
		Bridge = bridge;
		Platform = platform;
		Core = core;
		SourceHttp = sourceHttp;
		SourcesDir = sourcesDir;
	}

	public FakeHostBridge Bridge { get; }
	public TestPlatform Platform { get; }
	public HostCore Core { get; }

	/// <summary>The stub backing the source system's HTTP calls (the Notion token validate + API); set its responder per test.</summary>
	public StubHttpMessageHandler SourceHttp { get; }

	/// <summary>The temp <c>sources</c> dir — write a <c>notion.json</c> credentials file here to exercise the connect flow.</summary>
	public string SourcesDir { get; }

	/// <summary>The primary checkout (a git repo) this host is rooted at.</summary>
	public string RepoRoot { get; }

	/// <summary>Builds a temp git repo, starts a host over it, and delivers the page's <c>ready</c> message.</summary>
	public static async Task<TestHost> StartAsync() {
		string tempRoot = Path.Combine(Path.GetTempPath(), "weavie-host-it-" + Guid.NewGuid().ToString("n"));
		string repo = Path.Combine(tempRoot, "repo");
		Directory.CreateDirectory(repo);
		RunGit(repo, "init", "-b", "main");
		File.WriteAllText(Path.Combine(repo, "readme.txt"), "hello\n");
		RunGit(repo, "add", "-A");
		RunGit(repo, "-c", "user.email=test@weavie.dev", "-c", "user.name=Weavie Test", "-c", "commit.gpgsign=false", "commit", "-m", "initial");

		EnsureRelayBinary();

		var sourceHttp = new StubHttpMessageHandler();
		string sourcesDir = Path.Combine(tempRoot, "sources");
		var services = IsolatedServices(tempRoot, sourceHttp, sourcesDir);
		var bridge = new FakeHostBridge();
		var platform = new TestPlatform(bridge);
		var core = new HostCore(platform, services, repo);
		await core.StartAsync("http://127.0.0.1:65111").ConfigureAwait(false);
		// `ready` triggers the initial layout / editor-session / session-list pushes (PostToWeb no-ops before this).
		bridge.Receive("""{"type":"ready"}""");
		return new TestHost(tempRoot, repo, services, bridge, platform, core, sourceHttp, sourcesDir);
	}

	/// <summary>The primary session's id (its rail slot id), read from the initial set-editor-session sessionId.</summary>
	public string PrimaryId {
		get {
			var seed = Bridge.LastOfType("set-editor-session");
			return seed?.GetProperty("sessionId").GetString() ?? throw new InvalidOperationException("no set-editor-session seed");
		}
	}

	/// <summary>Creates a worktree-backed session on <paramref name="branch"/> (off main) and switches to it.</summary>
	public async Task<CommandResult> CreateSessionAsync(string branch) =>
		await Core.NewSessionAsync(new NewSessionRequest { Branch = branch, Base = "main", AttachExisting = false }, CancellationToken.None)
			.ConfigureAwait(false);

	/// <summary>Sends a raw web message to the host (as the page would).</summary>
	public void Send(string json) => Bridge.Receive(json);

	private static HostServices IsolatedServices(string tempRoot, StubHttpMessageHandler sourceHttp, string sourcesDir) {
		var settings = CoreSettings.CreateStore(Path.Combine(tempRoot, "settings.toml"), enableWatcher: false);
		var registry = CoreCommands.CreateRegistry();
		var keybindings = new KeybindingStore(registry, Path.Combine(tempRoot, "keybindings.json"), enableWatcher: false);
		var themeOverrides = new ThemeOverridesStore(new LocalFileSystem(), Path.Combine(tempRoot, "theme-overrides.json"));
		var claudeSessions = new ClaudeSessionStore(new LocalFileSystem(), Path.Combine(tempRoot, "claude-sessions.json"));
		var remoteAgents = new RemoteAgentStore(new LocalFileSystem(), Path.Combine(tempRoot, "remote-agents.json"));
		var railState = new RailStateStore(new LocalFileSystem(), Path.Combine(tempRoot, "rail-state.json"));
		return new HostServices {
			Settings = settings,
			CommandRegistry = registry,
			SuggestionRegistry = Weavie.Core.Suggestions.CoreSuggestions.CreateRegistry(),
			Keybindings = keybindings,
			ThemeOverrides = themeOverrides,
			ClaudeSessions = claudeSessions,
			RemoteAgents = remoteAgents,
			RailState = railState,
			PullRequests = new Weavie.Core.Review.StaticPullRequestProvider([], []),
			ReviewComments = new Weavie.Core.Review.StaticPullRequestProvider([], []),
			Sources = BuildSourceConnector(sourceHttp, sourcesDir),
			// A fresh, uninstalled buffer — tests never tee Console (that would hijack the xunit console).
			LogBuffer = new LogBuffer(LogBuffer.DefaultCapacity),
		};
	}

	// A source connector wired to the stub HTTP handler + temp token paths, so connect/fetch journeys run
	// deterministically and never touch the real ~/.weavie or the network.
	private static Weavie.Core.Sources.SourceConnector BuildSourceConnector(StubHttpMessageHandler sourceHttp, string sourcesDir) {
		var http = new HttpClient(sourceHttp);
		return new Weavie.Core.Sources.SourceConnector(
			[new Weavie.Core.Sources.NotionSource(http)], id => Path.Combine(sourcesDir, $"{id}.json"));
	}

	// IdeIntegration.WriteSettingsFile throws if the hook relay isn't co-located with the app; in a test run the
	// "app" is the test host, so drop a stub next to it (it's never executed — claude is launched through the
	// no-op PTY, which never starts).
	private static void EnsureRelayBinary() {
		string name = OperatingSystem.IsWindows() ? "weavie-hook-relay.exe" : "weavie-hook-relay";
		string path = Path.Combine(AppContext.BaseDirectory, name);
		if (!File.Exists(path)) {
			File.WriteAllText(path, "stub");
		}
	}

	private static void RunGit(string cwd, params string[] args) {
		var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardError = true, RedirectStandardOutput = true };
		foreach (string arg in args) {
			psi.ArgumentList.Add(arg);
		}

		using var process = Process.Start(psi) ?? throw new InvalidOperationException("git failed to start");
		process.WaitForExit();
		if (process.ExitCode != 0) {
			throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {process.StandardError.ReadToEnd()}");
		}
	}

	public async ValueTask DisposeAsync() {
		await Core.DisposeAsync().ConfigureAwait(false);
		_services.Keybindings.Dispose();
		_services.Settings.Dispose();
		try {
			Directory.Delete(_tempRoot, recursive: true);
		} catch (IOException) {
			// Best-effort temp cleanup; a lingering handle on Windows just leaves a temp dir behind.
		} catch (UnauthorizedAccessException) {
			// ditto
		}
	}
}

/// <summary>The thinnest <see cref="IHostPlatform"/>: a fake bridge, inline dispatch, no-op PTYs, no native UI.</summary>
internal sealed class TestPlatform : IHostPlatform {
	public TestPlatform(IHostBridge bridge) {
		Bridge = bridge;
		Dispatcher = new InlineUiDispatcher();
		PtyLauncher = new NoopPtyLauncher();
	}

	public IHostBridge Bridge { get; }
	public IUiDispatcher Dispatcher { get; }
	public IPtyLauncher PtyLauncher { get; }
	public string ChromePlatform => "web";
	public string? TitleBar => null;
	public IReadOnlyList<string> Recents => [];
	public IShellWindow? Window => null;
	public IGlobalHotkeyRegistrar? HotkeyRegistrar => null;
	public IHostDialogs? Dialogs => null;

	/// <summary>The last text the host wrote to the clipboard (a terminal copy / OSC 52).</summary>
	public string? LastWrittenClipboard { get; private set; }

	/// <summary>The last URL the host was asked to open externally.</summary>
	public string? LastOpenedUrl { get; private set; }

	/// <summary>The text a clipboard read returns (a terminal paste); set by a test.</summary>
	public string ClipboardValue { get; set; } = string.Empty;

	public void ToggleWindow() {
		// no window in tests
	}

	public void WriteClipboard(string text) => LastWrittenClipboard = text;

	public string ReadClipboard() => ClipboardValue;

	public void OpenExternalUrl(string url) => LastOpenedUrl = url;
}

/// <summary>A launcher whose terminals never spawn — sessions construct fine, but no real claude/shell runs.</summary>
internal sealed class NoopPtyLauncher : IPtyLauncher {
	public ITerminal CreateTerminal() => new NoopTerminal();

	public PtyLaunch Resolve(PtyLaunchRequest request) => new() {
		Command = "noop",
		Arguments = [],
		RemoveEnvironment = [],
		Environment = new Dictionary<string, string>(StringComparer.Ordinal),
	};
}

/// <summary>An <see cref="ITerminal"/> that does nothing — the child is never actually launched in tests.</summary>
internal sealed class NoopTerminal : ITerminal {
	public event Action<byte[]>? Output;
	public event Action<int>? Exited;

	public bool IsRunning => false;

	public void Start(TerminalStartInfo startInfo) {
		_ = Output;
		_ = Exited;
	}

	public void Write(byte[] data) {
		// no child to write to
	}

	public void Resize(int columns, int rows) {
		// no PTY to resize
	}

	public void Dispose() {
		// nothing to release
	}
}
