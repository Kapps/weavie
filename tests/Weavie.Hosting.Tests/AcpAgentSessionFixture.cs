using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Weavie.AgentClientProtocol;
using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Layout;
using Weavie.Core.Mcp;
using Weavie.Core.Sessions;
using Weavie.Core.Theming;

namespace Weavie.Hosting.Tests;

internal sealed class AcpAgentSessionFixture : IAsyncDisposable {
	private readonly SettingsStore _settings;
	private readonly KeybindingStore _keybindings;
	private readonly Channel<AgentPaneMessage> _pane = Channel.CreateUnbounded<AgentPaneMessage>();
	private readonly Channel<IReadOnlyList<AgentPaneMessage>> _snapshots =
		Channel.CreateUnbounded<IReadOnlyList<AgentPaneMessage>>();
	private readonly Channel<AgentControlState> _controls = Channel.CreateUnbounded<AgentControlState>();
	private readonly Channel<IReadOnlyList<AgentTurnSubmission>> _queues =
		Channel.CreateUnbounded<IReadOnlyList<AgentTurnSubmission>>();
	private readonly IAgentAuthenticationTerminal _authenticationTerminal;
	private readonly Lock _messageGate = new();
	private readonly List<AgentPaneMessage> _messages = [];

	private AcpAgentSessionFixture(
		string root,
		SettingsStore settings,
		KeybindingStore keybindings,
		EditorStore editor,
		AcpSessionStore sessions,
		AcpAgentSession session,
		RecordingAgentEventSink events,
		IAgentAuthenticationTerminal authenticationTerminal) {
		Workspace = root;
		_settings = settings;
		_keybindings = keybindings;
		Editor = editor;
		Sessions = sessions;
		Session = session;
		Events = events;
		_authenticationTerminal = authenticationTerminal;
		session.PaneMessage += message => {
			lock (_messageGate) _messages.Add(message);
			_pane.Writer.TryWrite(message);
		};
		session.PaneSnapshot += snapshot => _snapshots.Writer.TryWrite(snapshot);
		session.ControlStateChanged += state => _controls.Writer.TryWrite(state);
		session.QueuedSubmissionsChanged += queued => _queues.Writer.TryWrite(queued);
	}

	public AcpAgentSession Session { get; }
	public AcpSessionStore Sessions { get; }
	public EditorStore Editor { get; }
	public RecordingAgentEventSink Events { get; }
	public string Workspace { get; }
	public string FakeAcpStateDirectory => Path.Combine(Workspace, "weavie", "fake-acp-state");
	public AgentLaunch? AuthenticationLaunch =>
		(_authenticationTerminal as RecordingAuthenticationTerminal)?.Launch;

	public IReadOnlyList<AgentPaneMessage> Messages {
		get {
			lock (_messageGate) return [.. _messages];
		}
	}

	public static AcpAgentSessionFixture Create(bool allowAllPermissions, string? persistedSessionId) {
		return Create(
			"fake",
			"Fake ACP",
			ExecutablePath("tools", "Weavie.FakeAcp", "weavie-fake-acp"),
			new Dictionary<string, string>(StringComparer.Ordinal),
			allowAllPermissions,
			persistedSessionId,
			failSessionPersistence: false);
	}

	public static AcpAgentSessionFixture CreateWithFailingPersistence(bool allowAllPermissions) => Create(
		"fake",
		"Fake ACP",
		ExecutablePath("tools", "Weavie.FakeAcp", "weavie-fake-acp"),
		new Dictionary<string, string>(StringComparer.Ordinal),
		allowAllPermissions,
		persistedSessionId: null,
		failSessionPersistence: true);

	public static AcpAgentSessionFixture CreateMixedRequestIdAdapter() => Create(
		"fake",
		"Mixed request ID ACP",
		ExecutablePath("tools", "Weavie.FakeAcp", "weavie-fake-acp"),
		new Dictionary<string, string>(StringComparer.Ordinal) {
			["WEAVIE_FAKE_ACP_MODE"] = "mixed-request-ids",
		},
		allowAllPermissions: true,
		persistedSessionId: null,
		failSessionPersistence: false);

	public static AcpAgentSessionFixture CreateImmediatelyMalformedAdapter() => Create(
		"fake",
		"Malformed ACP",
		ExecutablePath("tools", "Weavie.FakeAcp", "weavie-fake-acp"),
		new Dictionary<string, string>(StringComparer.Ordinal) {
			["WEAVIE_FAKE_ACP_MODE"] = "immediate-malformed",
		},
		allowAllPermissions: true,
		persistedSessionId: null,
		failSessionPersistence: false);

	public static AcpAgentSessionFixture CreateHeldAuthenticationAdapter() => Create(
		"fake",
		"Authentication ACP",
		ExecutablePath("tools", "Weavie.FakeAcp", "weavie-fake-acp"),
		new Dictionary<string, string>(StringComparer.Ordinal) {
			["WEAVIE_FAKE_ACP_MODE"] = "held-authentication",
		},
		allowAllPermissions: true,
		persistedSessionId: null,
		failSessionPersistence: false);

	public static AcpAgentSessionFixture CreateAgentAuthenticationAdapter() => Create(
		"fake",
		"Authentication ACP",
		ExecutablePath("tools", "Weavie.FakeAcp", "weavie-fake-acp"),
		new Dictionary<string, string>(StringComparer.Ordinal) {
			["WEAVIE_FAKE_ACP_MODE"] = "agent-authentication",
		},
		allowAllPermissions: true,
		persistedSessionId: null,
		failSessionPersistence: false);

	public static AcpAgentSessionFixture CreateSideHeldAuthenticationAdapter() => Create(
		"fake",
		"Side authentication ACP",
		ExecutablePath("tools", "Weavie.FakeAcp", "weavie-fake-acp"),
		new Dictionary<string, string>(StringComparer.Ordinal) {
			["WEAVIE_FAKE_ACP_MODE"] = "side-held-authentication",
		},
		allowAllPermissions: true,
		persistedSessionId: null,
		failSessionPersistence: false);

	public static AcpAgentSessionFixture CreateMirroredModeAdapter() => Create(
		"fake",
		"Mirrored mode ACP",
		ExecutablePath("tools", "Weavie.FakeAcp", "weavie-fake-acp"),
		new Dictionary<string, string>(StringComparer.Ordinal) {
			["WEAVIE_FAKE_ACP_MODE"] = "mirrored-mode",
		},
		allowAllPermissions: true,
		persistedSessionId: null,
		failSessionPersistence: false);

	public static AcpAgentSessionFixture CreateMinimalCapabilitiesAdapter() => Create(
		"fake",
		"Minimal ACP",
		ExecutablePath("tools", "Weavie.FakeAcp", "weavie-fake-acp"),
		new Dictionary<string, string>(StringComparer.Ordinal) {
			["WEAVIE_FAKE_ACP_MODE"] = "minimal-capabilities",
		},
		allowAllPermissions: true,
		persistedSessionId: null,
		failSessionPersistence: false);

	public static AcpAgentSessionFixture CreateResumeOnlyAdapter(string persistedSessionId, long turnNumber) {
		var fixture = Create(
			"fake",
			"Resume-only ACP",
			ExecutablePath("tools", "Weavie.FakeAcp", "weavie-fake-acp"),
			new Dictionary<string, string>(StringComparer.Ordinal) {
				["WEAVIE_FAKE_ACP_MODE"] = "resume-only",
			},
			allowAllPermissions: true,
			persistedSessionId: null,
			failSessionPersistence: false);
		fixture.Sessions.Adopt("fake", fixture.Workspace, persistedSessionId, turnNumber);
		return fixture;
	}

	public static AcpAgentSessionFixture CreateTerminalAuthenticationAdapter() {
		string executable = ExecutablePath("tools", "Weavie.FakeAcp", "weavie-fake-acp");
		var environment = new Dictionary<string, string>(StringComparer.Ordinal) {
			["WEAVIE_FAKE_ACP_MODE"] = "terminal-authentication",
		};
		return Create(
			"fake",
			"Terminal authentication ACP",
			executable,
			environment,
			allowAllPermissions: true,
			persistedSessionId: null,
			failSessionPersistence: false,
			new RecordingAuthenticationTerminal());
	}

	public static AcpAgentSessionFixture CreateHeldCloseAdapter() => Create(
		"fake",
		"Nonresponsive close ACP",
		ExecutablePath("tools", "Weavie.FakeAcp", "weavie-fake-acp"),
		new Dictionary<string, string>(StringComparer.Ordinal) {
			["WEAVIE_FAKE_ACP_MODE"] = "held-close",
		},
		allowAllPermissions: true,
		persistedSessionId: null,
		failSessionPersistence: false);

	public static AcpAgentSessionFixture CreateNonLaunchingAdapter(out string executable) {
		executable = Path.Combine(
			Path.GetTempPath(),
			$"weavie-invalid-acp-{Guid.NewGuid():N}{(OperatingSystem.IsWindows() ? ".exe" : string.Empty)}");
		File.WriteAllText(executable, "not an executable");
		if (!OperatingSystem.IsWindows()) {
			File.SetUnixFileMode(
				executable,
				UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}
		return Create(
			"fake",
			"Invalid ACP",
			executable,
			new Dictionary<string, string>(StringComparer.Ordinal),
			allowAllPermissions: true,
			persistedSessionId: null,
			failSessionPersistence: false);
	}

	private static AcpAgentSessionFixture Create(
		string providerId,
		string providerName,
		string executable,
		IReadOnlyDictionary<string, string> environment,
		bool allowAllPermissions,
		string? persistedSessionId,
		bool failSessionPersistence) => Create(
		providerId,
		providerName,
		executable,
		environment,
		allowAllPermissions,
		persistedSessionId,
		failSessionPersistence,
		UnavailableAgentAuthenticationTerminal.Instance);

	private static AcpAgentSessionFixture Create(
		string providerId,
		string providerName,
		string executable,
		IReadOnlyDictionary<string, string> environment,
		bool allowAllPermissions,
		string? persistedSessionId,
		bool failSessionPersistence,
		IAgentAuthenticationTerminal authenticationTerminal) {
		string root = Path.Combine(Path.GetTempPath(), "weavie-acp-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		var processEnvironment = new Dictionary<string, string>(environment, StringComparer.Ordinal) {
			["WEAVIE_ROOT"] = Path.Combine(root, "weavie"),
		};
		var fileSystem = new LocalFileSystem();
		var settings = CoreSettings.CreateStore(Path.Combine(root, "settings.toml"), enableWatcher: false);
		settings.Set(
			AgentSettings.AllowAllPermissions,
			JsonSerializer.SerializeToElement(allowAllPermissions));
		var commandRegistry = CoreCommands.CreateRegistry();
		var keybindings = new KeybindingStore(
			commandRegistry,
			Path.Combine(root, "keybindings.json"),
			enableWatcher: false);
		var editor = new EditorStore();
		var registry = new CapabilityRegistryHost(
			AgentSessionCredential.Create(),
			FakeDiffPresenter.AlwaysKeep(),
			[root],
			"weavie-test",
			settings,
			new LayoutStore(fileSystem, LayoutPanes.CreateRegistry(), Path.Combine(root, "layout.json")),
			editor,
			exposeIdeTools: true,
			new CommandDispatcher(commandRegistry),
			keybindings,
			new ThemeOverridesStore(fileSystem, Path.Combine(root, "theme-overrides.json")),
			static () => "test-session");
		IFileSystem sessionFileSystem = failSessionPersistence
			? new AtomicWriteFailureFileSystem(fileSystem)
			: fileSystem;
		var store = new AcpSessionStore(sessionFileSystem, Path.Combine(root, "acp-sessions.json"));
		var controls = new AcpControlStore(sessionFileSystem, Path.Combine(root, "acp-controls.json"));
		if (persistedSessionId is not null) store.Adopt(providerId, root, persistedSessionId, 0);
		var events = new RecordingAgentEventSink();
		var definition = new AcpAgentDefinition {
			Id = providerId,
			Name = providerName,
			Command = executable,
			Arguments = [],
			Environment = processEnvironment,
			Distribution = "custom",
		};
		var session = new AcpAgentSession(
			new AgentSessionContext {
				Settings = settings,
				Workspace = root,
				FileSystem = fileSystem,
				Registry = registry,
				DiffPresenter = FakeDiffPresenter.AlwaysKeep(),
				Editor = editor,
				Runtime = new HostRuntimeInfo(HostTransport.Local, Managed: false, "test"),
				Events = events,
				CurrentSessionId = static () => "test-session",
				AuthenticationTerminal = authenticationTerminal,
			},
			definition,
			store,
			controls,
			events.Logs.Enqueue);
		return new AcpAgentSessionFixture(
			root,
			settings,
			keybindings,
			editor,
			store,
			session,
			events,
			authenticationTerminal);
	}

	public async Task<AgentControlState> StartAsync() {
		Session.Start();
		return await WaitForControlsAsync(state => state.Axes.Count > 0).ConfigureAwait(false);
	}

	public async Task<AgentPaneMessage> WaitForMessageAsync(Func<AgentPaneMessage, bool> predicate) {
		ArgumentNullException.ThrowIfNull(predicate);
		lock (_messageGate) {
			if (_messages.LastOrDefault(predicate) is { } existing) return existing;
		}
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		while (true) {
			var message = await _pane.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
			if (predicate(message)) return message;
		}
	}

	public async Task<AgentControlState> WaitForControlsAsync(Func<AgentControlState, bool> predicate) {
		ArgumentNullException.ThrowIfNull(predicate);
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		while (true) {
			var state = await _controls.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
			if (predicate(state)) return state;
		}
	}

	public async Task<IReadOnlyList<AgentTurnSubmission>> WaitForQueueAsync(
		Func<IReadOnlyList<AgentTurnSubmission>, bool> predicate) {
		ArgumentNullException.ThrowIfNull(predicate);
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		while (true) {
			var queued = await _queues.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
			if (predicate(queued)) return queued;
		}
	}

	public Task<IReadOnlyList<AgentPaneMessage>> WaitForSnapshotAsync() => ReadAsync(_snapshots.Reader);

	public void Submit(string text) => Session.Submit(new AgentTurnSubmission {
		Id = Guid.NewGuid().ToString("N"),
		Text = text,
		Kind = AgentTurnSubmissionKind.Prompt,
		CommandName = string.Empty,
		Attachments = [],
	});

	public void SubmitCommand(string name, string text) => Session.Submit(new AgentTurnSubmission {
		Id = Guid.NewGuid().ToString("N"),
		Text = text,
		Kind = AgentTurnSubmissionKind.ProviderCommand,
		CommandName = name,
		Attachments = [],
	});

	public async ValueTask DisposeAsync() {
		await Session.DisposeAsync().ConfigureAwait(false);
		await _authenticationTerminal.DisposeAsync().ConfigureAwait(false);
		_keybindings.Dispose();
		_settings.Dispose();
		Directory.Delete(Workspace, recursive: true);
	}

	private static async Task<T> ReadAsync<T>(ChannelReader<T> reader) {
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		return await reader.ReadAsync(timeout.Token).ConfigureAwait(false);
	}

	internal static string ExecutablePath(string topLevel, string project, string executable) {
		var targetDirectory = new DirectoryInfo(AppContext.BaseDirectory);
		string configuration = targetDirectory.Parent?.Name
			?? throw new InvalidOperationException("The test output configuration cannot be resolved.");
		string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
		return Path.Combine(
			root,
			topLevel,
			project,
			"bin",
			configuration,
			"net10.0",
			OperatingSystem.IsWindows() ? executable + ".exe" : executable);
	}

	private sealed class AtomicWriteFailureFileSystem(IFileSystem inner) : IFileSystem {
		public bool FileExists(string path) => inner.FileExists(path);
		public bool DirectoryExists(string path) => inner.DirectoryExists(path);
		public bool TryGetStat(string path, out FileStat stat) => inner.TryGetStat(path, out stat);
		public IReadOnlyList<DirectoryEntry> EnumerateDirectory(string path) => inner.EnumerateDirectory(path);
		public string ReadAllText(string path) => inner.ReadAllText(path);
		public bool TryReadAllText(string path, out string contents) => inner.TryReadAllText(path, out contents);
		public byte[] ReadAllBytes(string path) => inner.ReadAllBytes(path);
		public void WriteAllText(string path, string contents) => inner.WriteAllText(path, contents);
		public void WriteAllBytes(string path, byte[] contents) => inner.WriteAllBytes(path, contents);
		public void AppendAllText(string path, string contents) => inner.AppendAllText(path, contents);
		public void WriteAllTextAtomic(string path, string contents) =>
			throw new IOException("Synthetic ACP session persistence failure.");
		public void DeleteFile(string path) => inner.DeleteFile(path);
	}
}

internal sealed class RecordingAuthenticationTerminal : IAgentAuthenticationTerminal {
	public AgentLaunch? Launch { get; private set; }

	public Task<AgentProcessExit> RunAsync(AgentLaunch launch, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(launch);
		ct.ThrowIfCancellationRequested();
		Launch = launch;
		if (!launch.Arguments.Contains("terminal-login", StringComparer.Ordinal)
			|| !launch.Environment.TryGetValue("FAKE_LOGIN", out string? value)
			|| value != "1") {
			return Task.FromResult(new AgentProcessExit { ExitCode = 2, Unexpected = false });
		}
		File.WriteAllText(Path.Combine(launch.WorkingDirectory, "terminal-authenticated"), string.Empty);
		return Task.FromResult(new AgentProcessExit { ExitCode = 0, Unexpected = false });
	}

	public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class RecordingAgentEventSink : IAgentEventSink {
	private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>();
	private readonly Lock _blockGate = new();
	private RecordingAgentEventBlock? _block;

	public ConcurrentQueue<AgentEvent> Values { get; } = [];
	public ConcurrentQueue<string> Logs { get; } = [];
	public SessionStatusMachine Status { get; } = new();

	public RecordingAgentEventBlock BlockNext<T>() where T : AgentEvent {
		lock (_blockGate) {
			if (_block is not null) throw new InvalidOperationException("An event block is already armed.");
			_block = new RecordingAgentEventBlock(value => value is T);
			return _block;
		}
	}

	public AgentEventFeedback Observe(AgentEvent value) {
		RecordingAgentEventBlock? block = null;
		lock (_blockGate) {
			if (_block?.Matches(value) == true) {
				block = _block;
				_block = null;
			}
		}
		if (block is not null) {
			block.Enter();
			block.WaitForRelease();
		}
		Values.Enqueue(value);
		_events.Writer.TryWrite(value);
		Status.Observe(value);
		return AgentEventFeedback.None;
	}

	public async Task<AgentEvent> WaitForAsync(Func<AgentEvent, bool> predicate) {
		ArgumentNullException.ThrowIfNull(predicate);
		if (Values.LastOrDefault(predicate) is { } existing) return existing;
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		while (true) {
			var value = await _events.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
			if (predicate(value)) return value;
		}
	}
}

internal sealed class RecordingAgentEventBlock(Func<AgentEvent, bool> matches) {
	private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

	public Task Entered => _entered.Task;
	public bool Matches(AgentEvent value) => matches(value);
	public void Enter() => _entered.TrySetResult();
	public void Release() => _released.TrySetResult();
	public void WaitForRelease() => _released.Task.GetAwaiter().GetResult();
}
