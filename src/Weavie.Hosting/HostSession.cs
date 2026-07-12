using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Weavie.Core.Agents;
using Weavie.Core.Changes;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Corrections;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Hooks;
using Weavie.Core.Layout;
using Weavie.Core.Lsp;
using Weavie.Core.Mcp;
using Weavie.Core.Sessions;
using Weavie.Core.Shell;
using Weavie.Core.Theming;
using Weavie.Core.Workspaces;
using Weavie.Hosting.Agents;

namespace Weavie.Hosting;

/// <summary>
/// One Weavie session: the live, workspace-scoped backend an embedded agent works in — its two PTY terminals
/// (agent + shell), provider MCP integration, the LSP bridge, the file opener, and the Monaco diff
/// presenter, all rooted at a cwd given by constructor — so a worktree session is just one rooted at a different
/// path. Platform-agnostic: it talks to the page through <see cref="IHostBridge"/> and spawns its PTYs through an
/// injected <see cref="IPtyLauncher"/>; a <c>HostCore</c> owns a set of these and routes to the active one.
/// </summary>
public sealed class HostSession : IAsyncDisposable {
	private readonly IHostBridge _bridge;
	private readonly WorkspaceWatcher _watcher;
	// The server catalog advertised to the page (ids + language ids + default settings) — identical for every
	// session, so serialized once; LspConfigJson adds the per-session slot + worktree root.
	private static readonly string LspServersCatalogJson = JsonSerializer.Serialize(
		LanguageServerCatalog.All.Select(d => new {
			id = d.Id,
			languageIds = d.LanguageIds,
			settings = string.IsNullOrEmpty(d.DefaultSettingsJson) ? null : JsonNode.Parse(d.DefaultSettingsJson),
		}));

	/// <summary>
	/// Builds and starts the session's backend rooted at <paramref name="workspaceRoot"/>: terminals (via
	/// <paramref name="ptyLauncher"/>), the IDE-MCP + registry servers, and the LSP multiplexer.
	/// <paramref name="id"/> is this session's identity within its workspace;
	/// <paramref name="corrections"/> is the workspace's shared correction ring this session records into.
	/// </summary>
	public HostSession(
		IHostBridge bridge,
		SettingsStore settings,
		LayoutStore layout,
		string workspaceRoot,
		string scratchDir,
		string pastedImagesDir,
		string id,
		CommandRegistry commandRegistry,
		KeybindingStore keybindings,
		ThemeOverridesStore themeOverrides,
		CorrectionCorpus corrections,
		IPtyLauncher ptyLauncher,
		IAgentProvider agentProvider,
		HostRuntimeInfo runtime) {
		ArgumentNullException.ThrowIfNull(bridge);
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(layout);
		ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
		ArgumentException.ThrowIfNullOrEmpty(scratchDir);
		ArgumentException.ThrowIfNullOrEmpty(pastedImagesDir);
		ArgumentException.ThrowIfNullOrEmpty(id);
		ArgumentNullException.ThrowIfNull(commandRegistry);
		ArgumentNullException.ThrowIfNull(keybindings);
		ArgumentNullException.ThrowIfNull(themeOverrides);
		ArgumentNullException.ThrowIfNull(corrections);
		ArgumentNullException.ThrowIfNull(ptyLauncher);
		ArgumentNullException.ThrowIfNull(agentProvider);
		ArgumentNullException.ThrowIfNull(runtime);

		Id = id;
		WorkspaceRoot = workspaceRoot;
		_bridge = bridge;

		// Per-session command dispatcher over the app-global catalog: runCommand (MCP) and the web's
		// invoke-command both route here. The core wires the WebInvoker + Core handlers once the session exists.
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
		Browser = new WorkspaceBrowser(fileSystem, workspaceRoot);
		FileIndex = new WorkspaceFileIndex(fileSystem, workspaceRoot);
		Shell = new TerminalController(
			bridge, "shell", settings, ptyLauncher, new ShellTerminalProcess(settings, workspaceRoot)) {
			Workspace = workspaceRoot,
		};
		// The session's gate for editor-mutating page messages: a muted (non-active) session holds its editor work
		// instead of writing into the page's single, foreground-bound editor. Starts muted (HostCore activates it).
		EditorChannel = new SessionEditorChannel(bridge);
		FileOpener = new FileOpener(EditorChannel, FileProvider, bridge, workspaceRoot);
		DiffPresenter = new McpDiffPresenter(EditorChannel, FileProvider, FileOpener);
		// Tracks the editor's active file + selection (fed by the page) so the provider integration can tell
		// this session's agent what the user is looking at.
		Editor = new EditorStore();

		// Built before the IDE-MCP server so its EditLocationFor can back the hook bridge's edit jump-links. Scoped
		// to the roots the file provider serves (worktree + scratch), so an edit the agent makes outside this
		// session is never tracked and so never pushed as an unopenable diff.
		Changes = new SessionChangeTracker(
			fileSystem,
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
			bridge,
			settings,
			ptyLauncher);
		Claude = Agent.Terminal;
		// When the agent flips into an auto-apply mode (e.g. Shift+Tab to acceptEdits, clearing a pending openDiff in
		// the TUI), tear down any stale blocking openDiff — left alone it strands its review model over the editor
		// and blocks the post-turn review. Fires on the hook accept loop; EndDiff only touches the active session.
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

		// LSP: language servers spawned on demand and multiplexed over the SAME web bridge as the terminal — each
		// monaco-languageclient gets a (slot, channel) the host routes to its server's stdio. No socket/port/token of
		// its own, so language intelligence inherits the backend's transport (in-process, WebSocket, or a future
		// TLS-proxied one) and reaches remote sessions. The catalog is advertised in LspConfigJson so the page lazily
		// starts a client per language and feeds each server its defaults (e.g. gopls needs {"semanticTokens":true}).
		Lsp = new LspController(bridge, workspaceRoot, new LspServerLauncher(), LanguageServerCatalog.Resolve, Tagged("[lsp]"));
		// Watch the worktree for on-disk edits (agent or external): fan each debounced batch to the editor's
		// file:// provider (FileChanges) AND to the live language servers (didChangeWatchedFiles). Owned here, not by
		// the LSP layer, so it runs even with zero servers connected. Started eagerly.
		_watcher = new WorkspaceWatcher(workspaceRoot, LanguageServerCatalog.WatchedExtensions, OnWatchedChanges, Tagged("[lsp]"), debounceMs: 250);
		_watcher.Start();
	}

	/// <summary>This session's identity within its workspace.</summary>
	public string Id { get; }

	/// <summary>The directory this session's agent, shell, file opener, and LSP are rooted at.</summary>
	public string WorkspaceRoot { get; }

	/// <summary>The session's filesystem, used to persist the editor's autosaved buffers to disk.</summary>
	public IFileSystem FileSystem { get; }

	/// <summary>Serves the editor's host-backed <c>file://</c> provider (workspace-scoped fs-stat/read/write).</summary>
	public FileProviderService FileProvider { get; }

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

	/// <summary>The embedded-agent terminal, kept under the legacy <c>claude</c> wire id for compatibility when terminal-backed.</summary>
	public TerminalController? Claude { get; }

	/// <summary>The selected provider session and its compatibility terminal.</summary>
	public AgentSessionHost Agent { get; }

	/// <summary>The plain shell terminal.</summary>
	public TerminalController Shell { get; }

	/// <summary>Resolves a clicked <c>file:line</c> and pushes its contents to the editor.</summary>
	public FileOpener FileOpener { get; }

	/// <summary>Renders agent <c>openDiff</c> proposals to the Monaco diff view and resolves them.</summary>
	public McpDiffPresenter DiffPresenter { get; }

	/// <summary>
	/// The per-session gate for editor-mutating page messages, active only while this session drives the page; a
	/// muted session holds its show-diff/open-file/close-tab and replays it on switch-in.
	/// </summary>
	public SessionEditorChannel EditorChannel { get; }

	/// <summary>Routes command invocations (runCommand over MCP, invoke-command from the web) to Core/web handlers.</summary>
	public CommandDispatcher Commands { get; }

	/// <summary>Tracks the editor's active file + selection so the agent knows what the user is looking at.</summary>
	public EditorStore Editor { get; }

	/// <summary>
	/// This session's open editor tabs (paths + opaque view state), in memory for the window's lifetime. The page
	/// is the sole writer; the core pushes it as a <c>set-editor-session</c> on a switch so the editor rebinds to
	/// this session's worktree files. The primary also mirrors to the persisted store; worktree sessions don't.
	/// </summary>
	public EditorSession EditorSession { get; set; } = EditorSession.Empty;

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
	/// Raised with each debounced batch of on-disk changes under the worktree (forwarded to the editor's
	/// <c>file://</c> provider). Fires whether or not any language server is connected. Invoked off the UI thread.
	/// </summary>
	public event Action<IReadOnlyList<WatchedFileChange>>? FileChanges;

	/// <summary>
	/// The rail slot this session is bound to (empty until bound). The page tags its <c>lsp-*</c> frames with it so
	/// the host routes them to this session, and it is carried in <see cref="LspConfigJson"/> for the page to read.
	/// </summary>
	public string SlotId { get; private set; } = string.Empty;

	/// <summary>
	/// The <c>window.__WEAVIE_LSP__</c> / <c>lsp-config</c> discovery payload: the session's slot (frames are tagged
	/// with it), its worktree root, and the server catalog. No URL/token — LSP rides the bridge, not its own socket.
	/// </summary>
	public string LspConfigJson =>
		$"{{\"slot\":\"{JsonEncodedText.Encode(SlotId)}\",\"workspace\":\"{JsonEncodedText.Encode(WorkspaceRoot)}\",\"servers\":{LspServersCatalogJson}}}";

	/// <summary>
	/// Lists <paramref name="requestedPath"/> within the session root and pushes a <c>dir-listing</c> reply to the
	/// page (directories first). The file browser calls this on open and folder expand.
	/// </summary>
	public void ListDirectory(string requestedPath) {
		IReadOnlyList<BrowserEntry> entries;
		try {
			entries = Browser.List(requestedPath);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			// Surface the failure instead of letting it throw past the reply (which would hang the browser on
			// a folder that never fills); the page still gets an (empty) listing so its spinner resolves.
			entries = [];
			_bridge.PostToWeb(ShellProtocol.BuildNotify(
				"warn", $"Couldn't list {(string.IsNullOrEmpty(requestedPath) ? Browser.Root : requestedPath)}: {ex.Message}"));
		}

		string json = JsonSerializer.Serialize(new {
			type = "dir-listing",
			path = string.IsNullOrEmpty(requestedPath) ? Browser.Root : requestedPath,
			entries = entries.Select(e => new { name = e.Name, path = e.Path, isDir = e.IsDirectory }),
		});
		_bridge.PostToWeb(json);
	}

	/// <summary>
	/// Applies an <c>active-editor-changed</c> message from the page: updates the editor store, which pushes a
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

	/// <summary>
	/// Activates or mutes this session's editor output channel, flipped in lockstep with the active session so a
	/// background session never writes into the page's single editor. On activation it replays work held while
	/// muted (so a background openDiff surfaces on switch-in). Terminals need no such mute (each has its own pane).
	/// </summary>
	public void SetEditorOutputActive(bool active) {
		if (active) {
			EditorChannel.Activate();
		} else {
			EditorChannel.Deactivate();
			// No longer the foreground editor: drop the active-file/open-tab mirror so this session's agent
			// reports "no active editor", not a stale file. The page re-reports both on switch-in.
			Editor.Clear();
		}
	}

	/// <summary>
	/// Tags this session's terminal panes with their rail <paramref name="slotId"/>, so every <c>term-*</c>
	/// message names its session and the page routes it to that session's own xterm.
	/// </summary>
	public void BindTerminalsToSlot(string slotId) {
		SlotId = slotId;
		if (Claude is { } agentTerminal) {
			agentTerminal.SlotId = slotId;
		}
		Shell.SlotId = slotId;
	}

	/// <summary>Starts the active agent runtime.</summary>
	public void EnsureAgentStarted() {
		if (Claude is not null) {
			Claude.EnsureStarted();
		} else {
			Agent.Structured?.Start();
		}
	}

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
		if (Claude is not null) {
			Claude.Write(Encoding.UTF8.GetBytes(text));
			Claude.Write([(byte)'\r']);
			return;
		}

		Agent.Structured?.Submit(new AgentTurnSubmission {
			Id = Guid.NewGuid().ToString("n"),
			Text = text,
			Attachments = [],
			Skills = [],
		});
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

	// Fan a debounced watcher batch to the editor's file:// provider (so VSCode reloads externally-edited models)
	// and to this session's language servers (so their diagnostics/types don't go stale after an on-disk edit).
	private void OnWatchedChanges(IReadOnlyList<WatchedFileChange> changes) {
		FileChanges?.Invoke(changes);
		Lsp.NotifyWatchedFileChanges(changes);
	}

	/// <summary>
	/// Creates a new scratch (untitled) buffer under the workspace scratch dir and opens it as a scratch tab — the
	/// host side of New File (<c>Ctrl+N</c>).
	/// </summary>
	public void OpenNewScratch() {
		string path = Scratch.CreateNew();
		FileOpener.Open(path, 1, preview: false, scratch: true);
	}

	private static Action<string> Tagged(string tag) => line => {
		Console.WriteLine($"{tag} {line}");
		Console.Out.Flush();
	};

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		// Terminal disposal blocks until the PTY children exit (so a following worktree delete can't race a process
		// still rooted there). Run it off the calling (often UI) thread so a slow-closing child can't freeze the app.
		await Task.Run(() => {
			Claude?.Dispose();
			Shell.Dispose();
		}).ConfigureAwait(false);
		await Agent.DisposeProviderAsync().ConfigureAwait(false);
		_watcher.Dispose();
		// Pasted images are ephemeral prompt inputs — drop this session's on unload so they never accumulate.
		PastedImages.Clear();
		await Lsp.DisposeAsync().ConfigureAwait(false);
	}
}
