using System.Diagnostics;
using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Diagnostics;
using Weavie.Core.FileSystem;
using Weavie.Core.Mcp;
using Weavie.Core.Remote;
using Weavie.Core.Review;
using Weavie.Core.Search;
using Weavie.Core.Sessions;
using Weavie.Core.Shell;
using Weavie.Core.Terminal;
using Weavie.Core.Theming;
using Weavie.Hosting.Agents.Claude;
using Weavie.Hosting.Messaging;
using Weavie.Hosting.Web;

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
	internal const string TestPageId = "test-page";
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
	private readonly string _tempRoot;
	private readonly HostServices _services;
	private readonly Dictionary<SessionAddress, JsonElement> _clientEditorSessions = [];
	private long _requestSequence;
	private string _selectedSlot = "primary";

	private TestHost(string tempRoot, string repoRoot, HostServices services, FakeHostBridge bridge, TestPlatform platform, HostCore core, StubHttpMessageHandler sourceHttp, string sourcesDir) {
		_tempRoot = tempRoot;
		RepoRoot = repoRoot;
		_services = services;
		Bridge = bridge;
		Platform = platform;
		Core = core;
		SourceHttp = sourceHttp;
		SourcesDir = sourcesDir;
		bridge.RequestResponder = RespondToViewRequest;
	}

	public FakeHostBridge Bridge { get; private set; }
	public TestPlatform Platform { get; private set; }
	public HostCore Core { get; private set; }

	/// <summary>The stub backing the source system's HTTP calls (the Notion token validate + API); set its responder per test.</summary>
	public StubHttpMessageHandler SourceHttp { get; }

	/// <summary>The temp <c>sources</c> dir — write a <c>notion.json</c> credentials file here to exercise the connect flow.</summary>
	public string SourcesDir { get; }

	/// <summary>The primary checkout (a git repo) this host is rooted at.</summary>
	public string RepoRoot { get; }

	/// <summary>The isolated ring behind <c>weavie.view.logs</c> — append lines to exercise the log viewer.</summary>
	public LogBuffer LogBuffer => _services.LogBuffer;

	/// <summary>The host's settings store, for a test to tweak a setting before it creates a session.</summary>
	public SettingsStore Settings => _services.Settings;

	/// <summary>Builds a temp git repo, starts a host, and connects one message-bus client.</summary>
	public static Task<TestHost> StartAsync() => StartAsync(_ => { });

	/// <summary>
	/// As <see cref="StartAsync()"/>, with test-specific repo setup run BEFORE the host starts. Git commands
	/// that write the index (add / commit / checkout) must happen here: once the host is live its own git
	/// activity (status refresh) races a concurrent writer's <c>index.lock</c>.
	/// </summary>
	public static Task<TestHost> StartAsync(Action<string> prepareRepo) => StartAsync(prepareRepo, sendReady: true);

	/// <summary>As <see cref="StartAsync(Action{string})"/>, with deterministic pull requests exposed by the host.</summary>
	public static Task<TestHost> StartAsync(Action<string> prepareRepo, IReadOnlyList<PullRequestSummary> pullRequests) =>
		StartAsync(prepareRepo, new StaticPullRequestProvider(pullRequests, []), sendReady: true);

	/// <summary>As <see cref="StartAsync(Action{string})"/>, with a test-controlled pull request provider.</summary>
	public static Task<TestHost> StartAsync(Action<string> prepareRepo, IPullRequestProvider pullRequests) =>
		StartAsync(prepareRepo, pullRequests, sendReady: true);

	/// <summary>
	/// As <see cref="StartAsync(Action{string})"/>, but only delivers the page's <c>ready</c> message when
	/// <paramref name="sendReady"/> is true. Pass false to assert on host behavior BEFORE a page connects (e.g.
	/// that a startup push is held rather than dropped), then call <c>Send</c> with a <c>ready</c> message.
	/// </summary>
	public static Task<TestHost> StartAsync(Action<string> prepareRepo, bool sendReady) =>
		StartAsync(prepareRepo, new StaticPullRequestProvider([], []), sendReady);

	// Flaked 2026-07-19 ~16:09 UTC (https://github.com/Kapps/weavie/actions/runs/29694172917): every test in
	// this project failed from the very first one onward with FileNotFoundException loading
	// Microsoft.AspNetCore.Http out of HostCore.StartCoreAsync -> WorkspaceHttpServer.StartAsync. Investigated:
	// the identical SDK/runtime (10.0.302 / 10.0.10) on the very next main run
	// (https://github.com/Kapps/weavie/actions/runs/29695131021) passed clean, and the only commits in between
	// touched an unrelated web-bridge command and a different test's wait condition — nothing on this startup
	// path changed. Root cause is a one-off runner-side dotnet SDK extraction fault, not a code or test defect,
	// so no test-code change applies here.
	private static async Task<TestHost> StartAsync(Action<string> prepareRepo, IPullRequestProvider pullRequests, bool sendReady) {
		var host = Create(prepareRepo, pullRequests);
		await host.Core.StartAsync().ConfigureAwait(false);
		if (sendReady) {
			await host.ConnectAsync().ConfigureAwait(false);
		}

		return host;
	}

	/// <summary>Builds the real host graph without starting it, for startup/shutdown lifecycle tests.</summary>
	public static TestHost CreateUnstarted() => Create(_ => { }, new StaticPullRequestProvider([], []));

	/// <summary>Builds an unstarted host over a test-controlled UI dispatcher.</summary>
	public static TestHost CreateUnstarted(IUiDispatcher dispatcher) =>
		Create(_ => { }, new StaticPullRequestProvider([], []), dispatcher);

	private static TestHost Create(Action<string> prepareRepo, IPullRequestProvider pullRequests) =>
		Create(prepareRepo, pullRequests, new InlineUiDispatcher());

	private static TestHost Create(
		Action<string> prepareRepo,
		IPullRequestProvider pullRequests,
		IUiDispatcher dispatcher) {
		string tempRoot = Path.Combine(Path.GetTempPath(), "weavie-host-it-" + Guid.NewGuid().ToString("n"));
		string repo = Path.Combine(tempRoot, "repo");
		Directory.CreateDirectory(repo);
		RunGit(repo, "init", "--quiet", "-b", "main");
		File.WriteAllText(Path.Combine(repo, "readme.txt"), "hello\n");
		RunGit(repo, "add", "-A");
		RunGit(repo, "-c", "user.email=test@weavie.dev", "-c", "user.name=Weavie Test", "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "initial");
		prepareRepo(repo);

		EnsureRelayBinary();
		// Keep tests hermetic: never spawn the developer's real login shell or import its rc-file environment.
		LoginShellEnvironment.MarkImported();

		var sourceHttp = new StubHttpMessageHandler();
		string sourcesDir = Path.Combine(tempRoot, "sources");
		var services = IsolatedServices(tempRoot, sourceHttp, sourcesDir, pullRequests);
		var bridge = new FakeHostBridge();
		var platform = new TestPlatform(bridge, dispatcher);
		var core = new HostCore(
			platform,
			services,
			repo,
			WorkspaceHttpServerOptions.Native(Path.Combine(tempRoot, "wwwroot")),
			UnavailableWorkspaceWebSocketBridge.Instance);
		return new TestHost(tempRoot, repo, services, bridge, platform, core, sourceHttp, sourcesDir);
	}

	/// <summary>The primary live session's incarnation, used by media URLs.</summary>
	public string PrimaryIncarnation => PrimarySession.Incarnation;

	/// <summary>The primary live session.</summary>
	public HostSession PrimarySession => Session("primary");

	/// <summary>The session selected by this test client; selection never enters <see cref="HostCore"/>.</summary>
	public HostSession SelectedSession => Session(_selectedSlot);

	/// <summary>Returns one exact live session slot.</summary>
	public HostSession Session(string slot) =>
		Core.SessionForTest(slot) ?? throw new InvalidOperationException($"Session '{slot}' is not live.");

	/// <summary>Selects one exact live session in the test client and updates its host view binding.</summary>
	public void SelectSession(string slot) {
		var session = Session(slot);
		_selectedSlot = slot;
		SessionEvent(session, "view", "attach", new { pageEpoch = "test-page" });
	}

	/// <summary>Creates a worktree-backed session from this client's selected source and selects the result.</summary>
	public Task<CommandResult> CreateSessionAsync(string branch) =>
		CreateSessionAsync(new NewSessionRequest {
			Branch = branch,
			Base = "main",
			AttachExisting = false,
		});

	/// <summary>Creates a worktree-backed session from this client's selected source and selects the result.</summary>
	public async Task<CommandResult> CreateSessionAsync(NewSessionRequest request) {
		var result = await SelectedSession.Commands.InvokeAsync(
			SessionCommands.NewSession,
			JsonSerializer.Serialize(request, JsonOptions),
			CancellationToken.None).ConfigureAwait(false);
		if (result.Ok && request.Branch is { Length: > 0 } branch && Core.SessionForTest(branch) is not null) {
			SelectSession(branch);
		}

		return result;
	}

	/// <summary>Invokes a command through one exact session-owned dispatcher.</summary>
	public Task<CommandResult> InvokeCommandAsync(
		string slot,
		string command,
		object args,
		CancellationToken ct) =>
		Session(slot).Commands.InvokeAsync(command, JsonSerializer.Serialize(args, JsonOptions), ct);

	/// <summary>Invokes a command through this client's exact selected session bus.</summary>
	public async Task<CommandResult> InvokeClientCommandAsync(string command, object args) {
		var wire = await SessionRequestAsync<JsonElement>(
			SelectedSession,
			"commands",
			"invoke",
			new {
				id = command,
				args = JsonSerializer.SerializeToElement(args, JsonOptions),
			}).ConfigureAwait(false);
		string? dataJson = wire.TryGetProperty("data", out var data)
			&& data.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
				? data.GetRawText()
				: null;
		return new CommandResult(
			wire.GetProperty("ok").GetBoolean(),
			wire.GetProperty("message").ValueKind == JsonValueKind.String
				? wire.GetProperty("message").GetString()
				: null,
			wire.GetProperty("error").ValueKind == JsonValueKind.String
				? wire.GetProperty("error").GetString()
				: null) {
			DataJson = dataJson,
		};
	}

	/// <summary>Unloads one exact slot through the selected session's command dispatcher.</summary>
	public async Task<CommandResult> UnloadSessionAsync(string slot) {
		var result = await InvokeCommandAsync(
			_selectedSlot,
			SessionCommands.UnloadSession,
			new { id = slot },
			CancellationToken.None).ConfigureAwait(false);
		if (result.Ok && _selectedSlot == slot) {
			SelectSession("primary");
		}

		return result;
	}

	/// <summary>Deletes or classifies one exact slot through the selected session's command dispatcher.</summary>
	public Task<CommandResult> DeleteSessionAsync(string slot, bool force, bool classify) =>
		InvokeCommandAsync(
			_selectedSlot,
			SessionCommands.DeleteSession,
			new { id = slot, force, classify },
			CancellationToken.None);

	/// <summary>Performs the host hello and session sync sequence used by a real client connection.</summary>
	public async Task ConnectAsync() {
		var hello = await HostRequestAsync<JsonElement>("connection", "hello", new { }).ConfigureAwait(false);
		_selectedSlot = "primary";
		foreach (var entry in hello.GetProperty("sessions").EnumerateArray()) {
			if (!entry.TryGetProperty("address", out var address)
				|| address.ValueKind != JsonValueKind.Object) {
				continue;
			}

			var session = Session(entry.GetProperty("id").GetString()!);
			await SessionRequestAsync<JsonElement>(
				session,
				"lifecycle",
				"sync",
				new { }).ConfigureAwait(false);
		}

		SelectSession("primary");
	}

	/// <summary>Publishes a session event from the test client.</summary>
	public void SessionEvent(HostSession session, string feature, string name, object payload) {
		var element = JsonSerializer.SerializeToElement(payload, JsonOptions);
		if (feature == "editor"
			&& name == "sessionChanged"
			&& element.TryGetProperty("session", out var editorSession)) {
			_clientEditorSessions[session.Address] = editorSession.Clone();
		}

		SendEnvelope(MessageEnvelope.Event(
			MessageScope.Session,
			session.Address,
			feature,
			name,
			element).ToJson());
	}

	/// <summary>Publishes a host event from the test client.</summary>
	public void HostEvent(string feature, string name, object payload) =>
		SendEnvelope(MessageEnvelope.Event(
			MessageScope.Host,
			null,
			feature,
			name,
			JsonSerializer.SerializeToElement(payload, JsonOptions)).ToJson());

	/// <summary>Requests a response from one exact session.</summary>
	public Task<T> SessionRequestAsync<T>(
		HostSession session,
		string feature,
		string name,
		object payload) =>
		RequestAsync<T>(MessageScope.Session, session.Address, feature, name, payload);

	/// <summary>Requests a response from the host bus.</summary>
	public Task<T> HostRequestAsync<T>(string feature, string name, object payload) =>
		RequestAsync<T>(MessageScope.Host, null, feature, name, payload);

	private async Task<T> RequestAsync<T>(
		MessageScope scope,
		SessionAddress? address,
		string feature,
		string name,
		object payload) {
		string id = $"test-{Interlocked.Increment(ref _requestSequence)}";
		SendEnvelope(MessageEnvelope.Request(
			scope,
			address,
			id,
			feature,
			name,
			JsonSerializer.SerializeToElement(payload, JsonOptions)).ToJson());
		var response = await Wait.ForReferenceAsync(() => Response(id)).ConfigureAwait(false);
		if (response.Error is { } error) {
			throw new InvalidOperationException(error);
		}

		return response.Payload.Deserialize<T>(JsonOptions)!;
	}

	private MessageEnvelope? Response(string requestId) {
		foreach (string json in Bridge.Posted.Reverse()) {
			if (MessageEnvelope.TryParse(json, out var envelope)
				&& envelope is { Kind: MessageKind.Response }
				&& envelope.RequestId == requestId) {
				return envelope;
			}
		}

		return null;
	}

	private void SendEnvelope(string json) => Bridge.Receive(new WebPeer(TestPageId), json);

	private FakeWebResponse? RespondToViewRequest(MessageEnvelope request) {
		if (request.Feature != "editor" || request.Name != "flush" || request.Session is not { } address) {
			return null;
		}

		var session = _clientEditorSessions.TryGetValue(address, out var current)
			? current
			: JsonSerializer.SerializeToElement(new { active = (string?)null, open = Array.Empty<object>() });
		return new FakeWebResponse(
			JsonSerializer.SerializeToElement(new { session }, JsonOptions),
			null);
	}

	/// <summary>
	/// Simulates a worker restart (what a runner auto-update respawn does): disposes the live core and brings a
	/// fresh one up over the same repo — same workspace id, so it re-reads the persisted per-workspace stores.
	/// </summary>
	public Task RestartAsync() => RestartAsync(static () => {
	});

	/// <summary>
	/// Simulates a worker restart and lets tests mutate persisted state after shutdown, before the fresh core starts.
	/// </summary>
	public async Task RestartAsync(Action beforeRestart) {
		ArgumentNullException.ThrowIfNull(beforeRestart);
		await Core.DisposeAsync().ConfigureAwait(false);
		beforeRestart();
		Bridge = new FakeHostBridge();
		_requestSequence = 0;
		_selectedSlot = "primary";
		Platform = new TestPlatform(Bridge);
		Core = new HostCore(
			Platform,
			_services,
			RepoRoot,
			WorkspaceHttpServerOptions.Native(Path.Combine(_tempRoot, "wwwroot")),
			UnavailableWorkspaceWebSocketBridge.Instance);
		await Core.StartAsync().ConfigureAwait(false);
		await ConnectAsync().ConfigureAwait(false);
	}

	private static HostServices IsolatedServices(
		string tempRoot,
		StubHttpMessageHandler sourceHttp,
		string sourcesDir,
		IPullRequestProvider pullRequests) {
		var settings = CoreSettings.CreateStore(Path.Combine(tempRoot, "settings.toml"), enableWatcher: false);
		var registry = CoreCommands.CreateRegistry();
		var keybindings = new KeybindingStore(registry, Path.Combine(tempRoot, "keybindings.json"), enableWatcher: false);
		var themeOverrides = new ThemeOverridesStore(new LocalFileSystem(), Path.Combine(tempRoot, "theme-overrides.json"));
		var claudeSessions = new ClaudeSessionStore(new LocalFileSystem(), Path.Combine(tempRoot, "claude-sessions.json"));
		var agentProviders = new AgentProviderRegistry();
		agentProviders.Register(new ClaudeAgentProvider(claudeSessions));
		agentProviders.Register(new FakeCodexAgentProvider());
		var remoteAgents = new RemoteAgentStore(new LocalFileSystem(), Path.Combine(tempRoot, "remote-agents.json"));
		var railState = new RailStateStore(new LocalFileSystem(), Path.Combine(tempRoot, "rail-state.json"));
		var searchState = new SearchStateStore(new LocalFileSystem(), Path.Combine(tempRoot, "search-state.json"));
		return new HostServices {
			Settings = settings,
			CommandRegistry = registry,
			SuggestionRegistry = Weavie.Core.Suggestions.CoreSuggestions.CreateRegistry(),
			Keybindings = keybindings,
			ThemeOverrides = themeOverrides,
			AgentProviders = agentProviders,
			RemoteAgents = remoteAgents,
			RailState = railState,
			SearchState = searchState,
			PullRequests = pullRequests,
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

	internal static void RunGit(string cwd, params string[] args) {
		ProcessStartInfo psi = new("git") {
			WorkingDirectory = cwd,
			UseShellExecute = false,
		};
		foreach (string arg in args) {
			psi.ArgumentList.Add(arg);
		}

		using var process = Process.Start(psi) ?? throw new InvalidOperationException("git failed to start");
		process.WaitForExit();
		if (process.ExitCode != 0) {
			throw new InvalidOperationException($"git {string.Join(' ', args)} failed with exit code {process.ExitCode}.");
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

/// <summary>A fake bridge, configurable dispatcher, no-op PTYs, and no native UI.</summary>
internal sealed class TestPlatform : IHostPlatform {
	public TestPlatform(IWebTransportHub bridge) : this(bridge, new InlineUiDispatcher()) {
	}

	public TestPlatform(IWebTransportHub bridge, IUiDispatcher dispatcher) {
		ArgumentNullException.ThrowIfNull(dispatcher);
		Bridge = bridge;
		Dispatcher = dispatcher;
		NoopLauncher = new NoopPtyLauncher();
	}

	/// <summary>The typed launcher, so tests can reach the terminals it handed out.</summary>
	public NoopPtyLauncher NoopLauncher { get; }

	public IWebTransportHub Bridge { get; }
	public IUiDispatcher Dispatcher { get; }
	public IPtyLauncher PtyLauncher => NoopLauncher;
	public string ChromePlatform => "web";
	public HostTransport Transport => HostTransport.Local;
	public string? TitleBar => null;
	public IReadOnlyList<string> Recents => [];
	public IShellWindow? Window { get; set; }
	public IGlobalHotkeyRegistrar? HotkeyRegistrar => null;
	public IHostDialogs? Dialogs => null;

	/// <summary>The last text the host wrote to the clipboard (a terminal copy / OSC 52).</summary>
	public string? LastWrittenClipboard { get; private set; }

	/// <summary>The last URL the host was asked to open externally.</summary>
	public string? LastOpenedUrl { get; private set; }

	/// <summary>The text a clipboard read returns (a terminal paste); set by a test.</summary>
	public string ClipboardValue { get; set; } = string.Empty;

	/// <summary>The image a clipboard-image read returns (a claude-pane paste); set by a test. None by default.</summary>
	public ClipboardImage ClipboardImageValue { get; set; } = ClipboardImage.None;

	public int? ClipboardWriteThread { get; private set; }
	public int? ClipboardReadThread { get; private set; }
	public int? ClipboardImageReadThread { get; private set; }
	public int? OpenUrlThread { get; private set; }

	public void ToggleWindow() {
		// no window in tests
	}

	public void WriteClipboard(string text) {
		ClipboardWriteThread = Environment.CurrentManagedThreadId;
		LastWrittenClipboard = text;
	}

	public string ReadClipboard() {
		ClipboardReadThread = Environment.CurrentManagedThreadId;
		return ClipboardValue;
	}

	public ClipboardImage ReadClipboardImage() {
		ClipboardImageReadThread = Environment.CurrentManagedThreadId;
		return ClipboardImageValue;
	}

	public void OpenExternalUrl(string url) {
		OpenUrlThread = Environment.CurrentManagedThreadId;
		LastOpenedUrl = url;
	}
}

/// <summary>A launcher whose terminals never spawn — sessions construct fine, but no real claude/shell runs.</summary>
internal sealed class NoopPtyLauncher : IPtyLauncher {
	/// <summary>Every terminal handed out, in creation order — lets a test script one (e.g. its foreground-job flag).</summary>
	public List<NoopTerminal> Created { get; } = [];

	public ITerminal CreateTerminal() {
		var terminal = new NoopTerminal();
		Created.Add(terminal);
		return terminal;
	}

	public PtyLaunch Resolve(AgentLaunch launch) => new() {
		Command = launch.Command,
		Arguments = launch.Arguments,
		RemoveEnvironment = launch.RemoveEnvironment,
		Environment = launch.Environment,
	};
}

/// <summary>An <see cref="ITerminal"/> that does nothing — the child is never actually launched in tests.</summary>
internal sealed class NoopTerminal : ITerminal {
	public event Action<byte[]>? Output;
	public event Action<int>? Exited;

	public bool IsRunning => false;

	/// <summary>Test-scriptable foreground-job flag (the drain gate's shell probe).</summary>
	public bool HasForegroundJob { get; set; }

	/// <summary>How many input writes reached this terminal (asserts the drain input freeze).</summary>
	public int WriteCount { get; private set; }

	/// <summary>Every write's bytes, in order — lets a test assert what was injected (e.g. a bracketed paste).</summary>
	public List<byte[]> Writes { get; } = [];

	/// <summary>Every write concatenated and UTF-8 decoded — for asserting injected text.</summary>
	public string WrittenText => string.Concat(Writes.Select(System.Text.Encoding.UTF8.GetString));

	public void Start(TerminalStartInfo startInfo) {
		_ = Output;
		_ = Exited;
	}

	public void Write(byte[] data) {
		WriteCount++;
		Writes.Add(data);
	}

	public void Resize(int columns, int rows) {
		// no PTY to resize
	}

	public void Dispose() {
		// nothing to release
	}
}
