using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Weavie.Core.Agents;
using Weavie.Core.Changes;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Corrections;
using Weavie.Core.Editor;
using Weavie.Core.FileActivity;
using Weavie.Core.FileSystem;
using Weavie.Core.Hooks;
using Weavie.Core.Layout;
using Weavie.Core.Lsp;
using Weavie.Core.Mcp;
using Weavie.Core.Sessions;
using Weavie.Core.Theming;
using Weavie.Core.Workspaces;
using Weavie.Hosting.Agents;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

/// <summary>
/// One Weavie session: the live, workspace-scoped backend an embedded agent works in — its two PTY terminals
/// (agent + shell), provider MCP integration, the LSP bridge, the file opener, and the Monaco diff
/// presenter, all rooted at a cwd given by constructor — so a worktree session is just one rooted at a different
/// path. Platform-agnostic: it talks to the page through <see cref="IWebTransportHub"/> and spawns its PTYs through an
/// injected <see cref="IPtyLauncher"/>; a <c>HostCore</c> owns a set of exact-addressed session buses.
/// </summary>
public sealed partial class HostSession : IAsyncDisposable {
	private readonly SessionEndpoint _endpoint;
	private readonly MessageFeatureChannel _editorMessages;
	private readonly MessageFeatureChannel _notificationMessages;
	private readonly Lock _editorSessionGate = new();
	private readonly Lock _disposeGate = new();
	private EditorSession _editorSession = EditorSession.Empty;
	private Task? _disposeTask;
	private PullRequestStatusMonitor? _pullRequestStatus;
	private GitStatusMonitor? _gitStatus;
	private string? _workspaceWatcherFailure;
	// The server catalog advertised to the page (ids + language ids + default settings) — identical for every
	// session, so serialized once; LspConfigJson adds the per-session worktree root.
	private static readonly string LspServersCatalogJson = JsonSerializer.Serialize(
		LanguageServerCatalog.All.Select(d => new {
			id = d.Id,
			languageIds = d.LanguageIds,
			settings = string.IsNullOrEmpty(d.DefaultSettingsJson) ? null : JsonNode.Parse(d.DefaultSettingsJson),
		}));

	/// <summary>
	/// Builds and starts the session's backend rooted at <paramref name="workspaceRoot"/>: terminals (via
	/// <paramref name="ptyLauncher"/>), the IDE-MCP + registry servers, and the LSP multiplexer.
	/// <paramref name="endpoint"/> is this session's exact message-bus incarnation;
	/// <paramref name="corrections"/> is the workspace's shared correction ring this session records into.
	/// </summary>
	internal HostSession(
		SessionEndpoint endpoint,
		SettingsStore settings,
		LayoutStore layout,
		string workspaceRoot,
		string scratchDir,
		string pastedImagesDir,
		string agentPaneTranscriptPath,
		CommandRegistry commandRegistry,
		KeybindingStore keybindings,
		ThemeOverridesStore themeOverrides,
		CorrectionCorpus corrections,
		IPtyLauncher ptyLauncher,
		IAgentProvider agentProvider,
		HostRuntimeInfo runtime,
		Func<bool> inputFrozen,
		Action<int, int> shellResized) {
		ArgumentNullException.ThrowIfNull(endpoint);
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(layout);
		ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
		ArgumentException.ThrowIfNullOrEmpty(scratchDir);
		ArgumentException.ThrowIfNullOrEmpty(pastedImagesDir);
		ArgumentException.ThrowIfNullOrEmpty(agentPaneTranscriptPath);
		ArgumentNullException.ThrowIfNull(commandRegistry);
		ArgumentNullException.ThrowIfNull(keybindings);
		ArgumentNullException.ThrowIfNull(themeOverrides);
		ArgumentNullException.ThrowIfNull(corrections);
		ArgumentNullException.ThrowIfNull(ptyLauncher);
		ArgumentNullException.ThrowIfNull(agentProvider);
		ArgumentNullException.ThrowIfNull(runtime);
		ArgumentNullException.ThrowIfNull(inputFrozen);
		ArgumentNullException.ThrowIfNull(shellResized);

		_endpoint = endpoint;
		Background = new SessionTaskScope(Tagged("[session]"));
		State = new SessionState(Bus);
		DisplayLabel = endpoint.Address.Slot;
		WorkspaceRoot = workspaceRoot;
		_editorMessages = Bus.Feature("editor");
		_notificationMessages = Bus.Feature("notifications");

		// Per-session command dispatcher over the app-global catalog: runCommand (MCP) and this bus's
		// commands.invoke requests both route here. Core wires the WebInvoker + Core handlers once the session exists.
		Commands = new CommandDispatcher(commandRegistry);

		var fileSystem = new LocalFileSystem();
		FileSystem = fileSystem;
		// Scratch (untitled) buffers live in a per-workspace dir outside the workspace, so they never reach the
		// file tree/index/git/agent. The file provider gets that dir as a second allowed root so the editor can
		// read/write them as ordinary working copies.
		Scratch = new ScratchStore(fileSystem, scratchDir);
		// Images pasted into the agent land here (a scratch dir outside the workspace) and their path is injected into
		// the prompt; wiped on unload so they never linger or reach the tree/git.
		PastedImages = new PastedImageStore(fileSystem, pastedImagesDir);
		AgentAttachments = new AgentAttachmentStore(PastedImages);
		FileProvider = new FileProviderService(fileSystem, workspaceRoot, scratchDir);
		Inventory = new WorkspaceInventory(workspaceRoot);
		FileActivity = new SessionFileActivity(
			Inventory,
			Tagged("[files]"),
			watcherDebounceMs: 250);
		Browser = new WorkspaceBrowser(fileSystem, workspaceRoot);
		FileIndex = new WorkspaceFileIndex(fileSystem, workspaceRoot);
		Shell = new TerminalController(
			Bus.Feature("terminal.shell"),
			"shell",
			settings,
			ptyLauncher,
			new ShellTerminalProcess(settings, workspaceRoot)) {
			Workspace = workspaceRoot,
		};
		FileOpener = new FileOpener(
			View.Feature("view"),
			_notificationMessages,
			FileProvider,
			FileIndex,
			PublishEditorFileOpen);
		DiffPresenter = new McpDiffPresenter(
			_editorMessages,
			FileProvider,
			FileOpener,
			PublishEditorClose);
		// Tracks the editor's active file + selection (fed by the page) so the provider integration can tell
		// this session's agent what the user is looking at.
		Editor = new EditorStore();

		// Built before the IDE-MCP server so its EditLocationFor can back the hook bridge's edit jump-links. Scoped
		// to the roots the file provider serves (worktree + scratch), so an edit the agent makes outside this
		// session is never tracked and so never pushed as an unopenable diff.
		Changes = new SessionChangeTracker(
			fileSystem,
			FileActivity,
			workspaceRoot,
			path => BufferStore.IsWithinWorkspace(workspaceRoot, path) || BufferStore.IsWithinWorkspace(scratchDir, path));
		// Mirrors the provider's edit mode (default/acceptEdits/plan), observed off the event stream — Weavie
		// reflects it, never sets it. Drives the openDiff auto-keep + the post-turn review gating.
		ObservedMode = new ObservedPermissionMode();

		// Agent integration: start the provider-specific loopback server, render openDiff to Monaco, and expose
		// the standard registry tools to the embedded model.
		Status = new SessionStatusMachine();
		// Appends the user's corrections (editor saves over an agent hunk, and review-UI reverts) into the
		// workspace's shared ring, one entry per action — captured at the moment they act, not at a boundary.
		// See docs/specs/learn-from-corrections.md.
		Corrections = new CorrectionRecorder(corrections);
		Changes.Corrected += Corrections.Record;
		var eventRouter = new AgentEventRouter(Changes, ObservedMode, Status);
		Events = eventRouter;
		var agentDiffPresenter = new PermissionModeDiffPresenter(DiffPresenter, ObservedMode);
		bool exposeRegistryIdeTools = agentProvider.Info.Capabilities.HasFlag(AgentProviderCapabilities.StructuredPane);
		var registry = new CapabilityRegistryHost(
			AgentSessionCredential.Create(),
			agentDiffPresenter,
			[workspaceRoot],
			"weavie",
			settings,
			layout,
			Editor,
			exposeRegistryIdeTools,
			Commands,
			keybindings,
			themeOverrides,
			() => SlotId);

		Agent = new AgentSessionHost(
			agentProvider,
			new AgentSessionContext {
				Settings = settings,
				Workspace = workspaceRoot,
				FileSystem = fileSystem,
				Registry = registry,
				DiffPresenter = agentDiffPresenter,
				Editor = Editor,
				Runtime = runtime,
				Events = eventRouter,
				CurrentSessionId = () => SlotId,
			},
				Bus.Feature("agent"),
				Bus.Feature("terminal.agent"),
				settings,
			ptyLauncher,
			agentPaneTranscriptPath);
		Claude = Agent.Terminal;
		// When the agent flips into an auto-apply mode (e.g. Shift+Tab to acceptEdits, clearing a pending openDiff in
		// the TUI), tear down any stale blocking openDiff — left alone it strands its review model over the editor
		// and blocks the post-turn review. Each presenter only touches its owning session.
		ObservedMode.Changed += () => {
			if (ObservedMode.AutoAppliesEdits) {
				DiffPresenter.DismissPending();
			}
		};
		// The agent pane's input stream resolves an answered permission prompt (no hook fires at approval;
		// the tool only reports back at PostToolUse — minutes later for a long build).
		if (Claude is not null) {
			Claude.InputWritten += Status.ObserveUserInput;
			Claude.SupervisorChanged += Status.ObserveSupervisor;
		}

		// LSP: language servers spawned on demand and multiplexed over the same session bus as the terminal — each
		// monaco-languageclient gets a channel that its owning session routes to server stdio. No socket/port/token of
		// its own, so language intelligence inherits the backend's transport (in-process, WebSocket, or a future
		// TLS-proxied one) and reaches remote sessions. The catalog is advertised in LspConfigJson so the page lazily
		// starts a client per language and feeds each server its defaults (e.g. gopls needs {"semanticTokens":true}).
		Lsp = new LspController(
			workspaceRoot,
			new LspServerLauncher(),
			LanguageServerCatalog.Resolve,
			Tagged("[lsp]"));
		Bus.PeerDisconnected += peer =>
			_ = Background.Run(_ => Lsp.DisconnectAsync(peer));
		WireMessages(inputFrozen, shellResized);
	}

	/// <summary>This live backend incarnation.</summary>
	public string Incarnation => Address.Incarnation;

	/// <summary>The immutable slot and live incarnation used by the router.</summary>
	internal SessionAddress Address => _endpoint.Address;

	/// <summary>The session-owned message bus.</summary>
	internal SessionMessageBus Bus => _endpoint.Bus;

	/// <summary>Starts this session's structured runtime and advertises its bus after its exact address enters
	/// the host catalog.</summary>
	internal void ActivateOwnedRuntimeAndMessages() {
		_endpoint.Activate();
		_ = Background.Run(RunWorkspaceObservationAsync);
		Agent.Structured?.Start();
	}

	private async Task RunWorkspaceObservationAsync(CancellationToken ct) {
		try {
			await FileActivity.RunObservingAsync(ct).ConfigureAwait(false);
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception ex) {
			string message = $"Workspace file watching stopped: {ex.Message}";
			Volatile.Write(ref _workspaceWatcherFailure, message);
			_notificationMessages.Publish("show", new {
				level = "error",
				message,
			});
			throw;
		}
	}

	internal void ReplayWorkspaceWatcherFailure(MessageTarget target) {
		if (Volatile.Read(ref _workspaceWatcherFailure) is { } message) {
			target.Feature("notifications").Publish("show", new { level = "error", message });
		}
	}

	/// <summary>The transient page presentation currently attached to this exact session.</summary>
	public SessionView View => _endpoint.View;

	/// <summary>Background work cancelled and drained with this session.</summary>
	internal SessionTaskScope Background { get; }

	internal PullRequestStatusMonitor PullRequestStatus =>
		_pullRequestStatus ?? throw new InvalidOperationException("Pull request status was not attached.");

	internal GitStatusMonitor GitStatus =>
		_gitStatus ?? throw new InvalidOperationException("Git status was not attached.");

	internal void AttachGitStatus(GitStatusMonitor monitor) {
		ArgumentNullException.ThrowIfNull(monitor);
		if (Interlocked.CompareExchange(ref _gitStatus, monitor, null) is not null) {
			throw new InvalidOperationException("Git status is already attached.");
		}
	}

	internal void AttachPullRequestStatus(PullRequestStatusMonitor monitor) {
		ArgumentNullException.ThrowIfNull(monitor);
		if (Interlocked.CompareExchange(ref _pullRequestStatus, monitor, null) is not null) {
			throw new InvalidOperationException("Pull request status is already attached.");
		}
	}

	internal SessionState State { get; }

	/// <summary>The directory this session's agent, shell, file opener, and LSP are rooted at.</summary>
	public string WorkspaceRoot { get; }

	/// <summary>The session's filesystem, used to persist the editor's autosaved buffers to disk.</summary>
	public IFileSystem FileSystem { get; }

	/// <summary>Serves the editor's host-backed <c>file://</c> provider through this session's files feature.</summary>
	public FileProviderService FileProvider { get; }

	/// <summary>Orders this session's completed file activity and owned workspace invalidations.</summary>
	public SessionFileActivity FileActivity { get; }

	/// <summary>Owns this workspace's scratch (untitled-buffer) directory; New File creates a file here.</summary>
	public ScratchStore Scratch { get; }

	/// <summary>Owns this session's pasted-image directory; an image pasted into the agent is written here and its path injected into the prompt.</summary>
	public PastedImageStore PastedImages { get; }

	/// <summary>Stages structured-agent attachments until an exact turn submission claims them.</summary>
	internal AgentAttachmentStore AgentAttachments { get; }

	/// <summary>Lists directories under the session root for the contextual file browser.</summary>
	public WorkspaceBrowser Browser { get; }

	/// <summary>Flat recursive file list under the session root, for the omnibar "Go to File" quick-open.</summary>
	public WorkspaceFileIndex FileIndex { get; }

	/// <summary>The session's authoritative Git-backed file and directory inventory.</summary>
	public WorkspaceInventory Inventory { get; }

	/// <summary>The selected provider's terminal-compatible agent pane, when it has one.</summary>
	public TerminalController? Claude { get; }

	/// <summary>The selected provider session and its compatibility terminal.</summary>
	public AgentSessionHost Agent { get; }

	/// <summary>The plain shell terminal.</summary>
	public TerminalController Shell { get; }

	/// <summary>Resolves a clicked <c>file:line</c> and pushes its contents to the editor.</summary>
	public FileOpener FileOpener { get; }

	/// <summary>Renders agent <c>openDiff</c> proposals to the Monaco diff view and resolves them.</summary>
	public McpDiffPresenter DiffPresenter { get; }

	/// <summary>Routes command invocations from MCP or this session's bus to Core/web handlers.</summary>
	public CommandDispatcher Commands { get; }

	/// <summary>Tracks the editor's active file + selection so the agent knows what the user is looking at.</summary>
	public EditorStore Editor { get; }

	/// <summary>
	/// This session's open editor tabs (paths + opaque view state), in memory for the window's lifetime. The page
	/// reports user-driven changes while host-driven opens mutate the same state. Its owning slot persists it.
	/// </summary>
	public EditorSession EditorSession {
		get { lock (_editorSessionGate) { return _editorSession; } }
		set {
			ArgumentNullException.ThrowIfNull(value);
			lock (_editorSessionGate) {
				_editorSession = value;
			}

			EditorSessionChanged?.Invoke(value);
		}
	}

	internal event Action<EditorSession>? EditorSessionChanged;

	internal void ReplayEditor(MessageTargetFeature target, Action<string> log) {
		ArgumentNullException.ThrowIfNull(target);
		ArgumentNullException.ThrowIfNull(log);
		lock (_editorSessionGate) {
			target.PublishJson(
				"restore",
				EditorSessionSerialization.BuildRestoreJson(
					_editorSession,
					FileSystem,
					WorkspaceRoot,
					log));
		}
	}

	private void PublishEditorFileOpen(
		string path,
		int line,
		bool preview,
		bool scratch) {
		EditorSession next;
		lock (_editorSessionGate) {
			next = RecordEditorOpenLocked(path, preview, scratch, kind: null);
			_editorMessages.Publish("openFile", new { path, line, preview, scratch });
		}

		EditorSessionChanged?.Invoke(next);
	}

	private EditorSession RecordEditorOpenLocked(
		string path,
		bool preview,
		bool scratch,
		string? kind) {
		EditorSession next;
		var current = _editorSession;
		var open = current.Open.ToList();
		int existing = open.FindIndex(entry => SameEditorPath(entry.Path, path));
		if (existing >= 0) {
			var entry = open[existing];
			if (entry.Preview && !preview) {
				open[existing] = entry with { Preview = false };
			}
		} else {
			var entry = new EditorSessionEntry {
				Path = path,
				Kind = kind,
				ViewState = null,
				Preview = preview,
				Scratch = scratch,
			};
			int priorPreview = preview
				? open.FindIndex(candidate => candidate.Preview)
				: -1;
			if (priorPreview >= 0) {
				open[priorPreview] = entry;
			} else {
				open.Add(entry);
			}
		}

		next = current with { Active = path, Open = open };
		_editorSession = next;
		return next;
	}

	private void PublishEditorClose(string path) {
		EditorSession? next = null;
		lock (_editorSessionGate) {
			var current = _editorSession;
			int index = current.Open.ToList().FindIndex(entry => SameEditorPath(entry.Path, path));
			if (index < 0) {
				return;
			}

			var open = current.Open.Where(entry => !SameEditorPath(entry.Path, path)).ToArray();
			string? active = current.Active;
			if (active is not null && SameEditorPath(active, path)) {
				active = open.Length == 0 ? null : open[Math.Min(index, open.Length - 1)].Path;
			}

			next = current with { Active = active, Open = open };
			_editorSession = next;
			_editorMessages.Publish("closeTab", new { path });
		}

		EditorSessionChanged?.Invoke(next);
	}

	internal void OpenEditorOverlay(string path, string kind) {
		EditorSession next;
		lock (_editorSessionGate) {
			next = RecordEditorOpenLocked(path, preview: false, scratch: false, kind);
			_editorMessages.Publish("openOverlay", new { path, kind });
		}

		EditorSessionChanged?.Invoke(next);
	}

	private static bool SameEditorPath(string left, string right) =>
		string.Equals(
			left,
			right,
			OperatingSystem.IsWindows()
				? StringComparison.OrdinalIgnoreCase
				: StringComparison.Ordinal);

	/// <summary>Records every file changed this session (diff vs. each file's session baseline).</summary>
	public SessionChangeTracker Changes { get; }

	/// <summary>Appends the user's corrections (editor saves over an agent hunk, and reverts) into the workspace's shared ring.</summary>
	public CorrectionRecorder Corrections { get; }

	/// <summary>The event sink provider integrations feed — the router fanning to tracker/mode/status.</summary>
	public IAgentEventSink Events { get; }

	/// <summary>The agent's edit mode (default/acceptEdits/plan), observed off provider events; Weavie reflects it, never sets it.</summary>
	public ObservedPermissionMode ObservedMode { get; }

	/// <summary>The live status of this session's agent (Starting/Working/NeedsInput/Idle/Error), for the rail.</summary>
	public SessionStatusMachine Status { get; }

	/// <summary>The LSP multiplexer rooted at this session's cwd, riding the web bridge.</summary>
	public LspController Lsp { get; }

	/// <summary>
	/// The rail slot this session owns.
	/// </summary>
	public string SlotId => Address.Slot;

	/// <summary>The latest catalog label for this session, used by owner-scoped notifications.</summary>
	internal string DisplayLabel { get; set; } = string.Empty;

	/// <summary>
	/// The session's LSP discovery payload: its worktree root and server catalog. Addressing belongs to the
	/// session message envelope, not the feature payload.
	/// </summary>
	public string LspConfigJson =>
		$"{{\"workspace\":\"{JsonEncodedText.Encode(WorkspaceRoot)}\",\"servers\":{LspServersCatalogJson}}}";

	/// <summary>
	/// Lists <paramref name="requestedPath"/> within the session root for the requesting file browser.
	/// </summary>
	private DirectoryListingMessage ListDirectory(string requestedPath) =>
		new([.. Browser.List(requestedPath).Select(
			entry => new DirectoryEntryMessage(entry.Name, entry.Path, entry.IsDirectory))]);

	/// <summary>
	/// Applies an editor <c>activeChanged</c> event from the page: updates the editor store, which pushes a
	/// <c>selection_changed</c> notification to the provider integration.
	/// </summary>
	public void UpdateActiveEditor(JsonElement message) {
		if (ActiveEditor.TryParse(message, out var editor) && editor is not null) {
			Editor.SetActive(editor);
		}
	}

	/// <summary>
	/// Applies an <c>open-editors-changed</c> message: records the full open-tab set so the IDE-MCP
	/// <c>getOpenEditors</c>/<c>close_tab</c> tools report and target the real tabs.
	/// </summary>
	public void UpdateOpenEditors(JsonElement message) =>
		Editor.SetOpenEditors(OpenEditorTab.ParseList(message));

	/// <summary>Restarts the active agent runtime when the provider supports process restart from Weavie.</summary>
	public void RestartAgent() {
		if (Claude is not null) {
			Claude.Restart();
			return;
		}

		Agent.Structured?.Restart();
	}

	/// <summary>Sends a prompt to the active agent using the provider's native input path.</summary>
	public void SendAgentPrompt(string text) {
		ArgumentNullException.ThrowIfNull(text);
		SendAgentInput(new AgentTurnSubmission {
			Id = Guid.NewGuid().ToString("n"),
			Text = text,
			Attachments = [],
			Skills = [],
		});
	}

	/// <summary>Sends one atomic input to the active agent using the provider's native input path.</summary>
	private void SendAgentInput(AgentTurnSubmission input) {
		if (Claude is not null) {
			foreach (var attachment in input.Attachments) {
				SendAgentImagePath(attachment.Path);
			}
			Claude.Write(Encoding.UTF8.GetBytes(input.Text));
			Claude.Write([(byte)'\r']);
			return;
		}

		Agent.Structured?.Submit(input);
	}

	/// <summary>Prefills a prompt in the active agent without submitting it, when the provider supports draft input.</summary>
	public void PrefillAgentPrompt(string text) {
		ArgumentNullException.ThrowIfNull(text);
		if (Claude is not null) {
			Claude.WriteBracketedPaste(text);
			return;
		}

		Agent.Structured?.PrefillPrompt(text);
	}

	/// <summary>Sends an image path to the active agent using the provider's native input path.</summary>
	public void SendAgentImagePath(string path) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		if (Claude is not null) {
			Claude.WriteBracketedPaste(path);
			return;
		}

		throw new InvalidOperationException("Structured agent images must be submitted as explicit attachments.");
	}

	/// <summary>
	/// Creates a new scratch (untitled) buffer under the workspace scratch dir and opens it as a scratch tab — the
	/// host side of New File (<c>Ctrl+N</c>).
	/// </summary>
	public void OpenNewScratch() {
		string path = Scratch.CreateNew();
		FileOpener.Open(path, 1, preview: false, scratch: true);
	}

	/// <summary>Reveals the exact completed agent plan in this session's editor channel.</summary>
	public bool OpenAgentPlan(string threadId, string turnId, string itemId) {
		if (!Agent.TryGetCompletedPlan(threadId, turnId, itemId, out var plan)) {
			return false;
		}

		string path = AgentPlanProtocol.Path(plan);
		State.Set("editor", $"plan:{plan.Id}", "agentPlan", AgentPlanProtocol.Show(plan, path));
		OpenEditorOverlay(path, "plan");
		return true;
	}

	private static Action<string> Tagged(string tag) => line => {
		Console.WriteLine($"{tag} {line}");
		Console.Out.Flush();
	};

	/// <inheritdoc/>
	public ValueTask DisposeAsync() {
		lock (_disposeGate) {
			return new ValueTask(_disposeTask ??= DisposeCoreAsync());
		}
	}

	private async Task DisposeCoreAsync() {
		DiscardInitialInput();
		var failures = new List<Exception>();
		await DisposeStepAsync(failures, () => _endpoint.QuiesceAsync()).ConfigureAwait(false);
		await DisposeStepAsync(failures, FileActivity.StopObservingAsync).ConfigureAwait(false);
		await DisposeStepAsync(failures, () => Background.DisposeAsync().AsTask()).ConfigureAwait(false);
		// Terminal disposal blocks until the PTY children exit (so a following worktree delete can't race a
		// process still rooted there). Keep it off the calling UI thread.
		await DisposeStepAsync(failures, () => Task.Run(() => Shell.Dispose())).ConfigureAwait(false);
		await DisposeStepAsync(failures, () => Agent.DisposeAsync().AsTask()).ConfigureAwait(false);
		await DisposeStepAsync(
			failures,
			() => FileActivity.DrainAsync(CancellationToken.None)).ConfigureAwait(false);
		await DisposeStepAsync(failures, () => FileActivity.DisposeAsync().AsTask()).ConfigureAwait(false);
		await DisposeStepAsync(failures, () => FileOpener.DisposeAsync().AsTask()).ConfigureAwait(false);
		await DisposeStepAsync(failures, () => Lsp.DisposeAsync().AsTask()).ConfigureAwait(false);
		await DisposeStepAsync(failures, () => {
			PastedImages.Clear();
			return Task.CompletedTask;
		}).ConfigureAwait(false);
		await DisposeStepAsync(failures, () => _endpoint.DisposeAsync().AsTask()).ConfigureAwait(false);
		if (failures.Count > 0) {
			throw new AggregateException(failures);
		}
	}

	private static async Task DisposeStepAsync(List<Exception> failures, Func<Task> step) {
		try {
			await step().ConfigureAwait(false);
		} catch (Exception ex) {
			failures.Add(ex);
		}
	}
}
