using System.Text.Json;
using System.Text.Json.Nodes;
using Weavie.Core.Changes;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
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

namespace Weavie.Hosting;

/// <summary>
/// One Weavie session: the live, workspace-scoped backend a single Claude works in — its two PTY terminals
/// (claude + shell), the IDE-MCP server + lock file, the LSP bridge, the file opener, and the Monaco diff
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
	/// <paramref name="id"/> is this session's identity within its workspace.
	/// </summary>
	public HostSession(
		IHostBridge bridge,
		SettingsStore settings,
		LayoutStore layout,
		string workspaceRoot,
		string scratchDir,
		string id,
		CommandRegistry commandRegistry,
		KeybindingStore keybindings,
		ThemeOverridesStore themeOverrides,
		IPtyLauncher ptyLauncher,
		ClaudeSessionStore claudeSessions) {
		ArgumentNullException.ThrowIfNull(bridge);
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(layout);
		ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
		ArgumentException.ThrowIfNullOrEmpty(scratchDir);
		ArgumentException.ThrowIfNullOrEmpty(id);
		ArgumentNullException.ThrowIfNull(commandRegistry);
		ArgumentNullException.ThrowIfNull(keybindings);
		ArgumentNullException.ThrowIfNull(themeOverrides);
		ArgumentNullException.ThrowIfNull(ptyLauncher);
		ArgumentNullException.ThrowIfNull(claudeSessions);

		Id = id;
		WorkspaceRoot = workspaceRoot;
		_bridge = bridge;

		// Per-session command dispatcher over the app-global catalog: runCommand (MCP) and the web's
		// invoke-command both route here. The core wires the WebInvoker + Core handlers once the session exists.
		Commands = new CommandDispatcher(commandRegistry);

		var fileSystem = new LocalFileSystem();
		FileSystem = fileSystem;
		// Scratch (untitled) buffers live in a per-workspace dir outside the workspace, so they never reach the
		// file tree/index/git/Claude. The file provider gets that dir as a second allowed root so the editor can
		// read/write them as ordinary working copies.
		Scratch = new ScratchStore(fileSystem, scratchDir);
		FileProvider = new FileProviderService(fileSystem, workspaceRoot, scratchDir);
		Browser = new WorkspaceBrowser(fileSystem, workspaceRoot);
		FileIndex = new WorkspaceFileIndex(fileSystem, workspaceRoot);
		Claude = new TerminalController(bridge, "claude", settings, ptyLauncher) {
			Workspace = workspaceRoot,
			// Resume this session's worktree's previous Claude conversation across launches (gated by the setting).
			ClaudeSessions = claudeSessions,
			// Only --resume when Claude still has the transcript, so a dead/relocated id cold-starts instead of looping.
			ClaudeTranscripts = new ClaudeTranscripts(fileSystem, Weavie.Core.ClaudeConfigPaths.ProjectsDirectory),
		};
		Shell = new TerminalController(bridge, "shell", settings, ptyLauncher) { Workspace = workspaceRoot };
		// The session's gate for editor-mutating page messages: a muted (non-active) session holds its editor work
		// instead of writing into the page's single, foreground-bound editor. Starts muted (HostCore activates it).
		EditorChannel = new SessionEditorChannel(bridge);
		FileOpener = new FileOpener(EditorChannel, FileProvider, bridge, workspaceRoot);
		DiffPresenter = new McpDiffPresenter(EditorChannel, FileProvider, FileOpener);
		// Tracks the editor's active file + selection (fed by the page) so the IDE-MCP server can tell
		// this session's claude what the user is looking at.
		Editor = new EditorStore();

		// Built before the IDE-MCP server so its EditLocationFor can back the hook bridge's edit jump-links. Scoped
		// to the roots the file provider serves (worktree + scratch), so an edit Claude makes outside this session
		// (e.g. its own ~/.claude config) is never tracked and so never pushed as an unopenable diff.
		Changes = new SessionChangeTracker(
			fileSystem,
			path => BufferStore.IsWithinWorkspace(workspaceRoot, path) || BufferStore.IsWithinWorkspace(scratchDir, path));
		// Mirrors Claude's own edit mode (default/acceptEdits/plan), observed off the hook stream — Weavie
		// reflects it, never sets it. Drives the openDiff auto-keep + the post-turn review gating.
		ObservedMode = new ObservedPermissionMode();

		// IDE-MCP: start the loopback server + lock file, render openDiff to Monaco, and inject the discovery env so
		// this session's claude connects to us. The same store backs the settings MCP tools (settings-by-talking).
		Ide = new IdeIntegration(
			new PermissionModeDiffPresenter(DiffPresenter, ObservedMode), [workspaceRoot], "weavie", settings, layout, Editor,
			commands: Commands, keybindings: keybindings, themeOverrides: themeOverrides,
			editLocator: Changes.EditLocationFor);
		Ide.Server.Log += Tagged("[mcp]");
		Ide.RegistryServer?.Log += Tagged("[registry]");

		Claude.ExtraEnvironment = Ide.EnvironmentVariables;
		// Capability registry: hand the spawned claude an --mcp-config pointing at the registry server
		// so the settings tools reach the model as mcp__weavie__* (the IDE server's tools are filtered).
		Claude.McpConfigPath = Ide.WriteMcpConfigFile();
		// Hook bridge: a --settings file whose hooks route claude's tool calls to our relay. The observed
		// stream is logged here; the session change view consumes the same feed.
		Claude.SettingsFilePath = Ide.WriteSettingsFile();
		// Orientation: an --append-system-prompt-file telling claude it's embedded in Weavie and to read
		// live app state (themes/settings/layout) through the mcp__weavie__* tools, not the on-disk config.
		Claude.SystemPromptFilePath = Ide.WriteSystemPromptFile();
		Ide.HookBridge.Observed += request => {
			Console.WriteLine($"[hook] {request.Event} {request.ToolName}");
			Console.Out.Flush();
		};
		Ide.HookBridge.Log += Tagged("[hook]");

		// Session change tracking: the same hook stream feeds the tracker (baseline at PreToolUse, new
		// content at PostToolUse). Because hooks fire before the permission check, this records edits in
		// every mode (default/acceptEdits/bypass) — independent of openDiff.
		Ide.HookBridge.Observed += Changes.Observe;
		// The same stream mirrors Claude's observed edit mode (its permission_mode field).
		Ide.HookBridge.Observed += ObservedMode.Observe;
		// When Claude flips into an auto-apply mode (e.g. Shift+Tab to acceptEdits, clearing a pending openDiff in
		// the TUI), tear down any stale blocking openDiff — left alone it strands its review model over the editor
		// and blocks the post-turn review. Fires on the hook accept loop; EndDiff only touches the active session.
		ObservedMode.Changed += () => {
			if (ObservedMode.AutoAppliesEdits) {
				DiffPresenter.DismissPending();
			}
		};
		// Keeps the resume store honest with what claude did: a /clear abandons the tracked id (next launch
		// cold-starts), and the next real message adopts the id claude settled on. The controller owns the policy.
		Ide.HookBridge.Observed += Claude.ObserveHook;

		// Per-session Claude status (the rail/pane indicator): the hook stream drives the live states, the gate's
		// decisions tell an about-to-show permission dialog (NeedsInput) from an auto-answered one, and the
		// claude supervisor drives crash/crash-loop → Error. All observers run on the hook accept-loop thread.
		Status = new SessionStatusMachine();
		Ide.HookBridge.Observed += Status.Observe;
		Ide.HookBridge.Decided += Status.ObserveDecision;
		// The claude pane's input stream resolves an answered permission prompt (no hook fires at approval;
		// the tool only reports back at PostToolUse — minutes later for a long build).
		Claude.InputWritten += Status.ObserveUserInput;
		Claude.SupervisorChanged += Status.ObserveSupervisor;
		Console.WriteLine($"[weavie] IDE-MCP on 127.0.0.1:{Ide.Port}; registry on 127.0.0.1:{Ide.RegistryPort}; workspace {workspaceRoot}; lock {Ide.LockFilePath}");
		Console.Out.Flush();

		// LSP: language servers spawned on demand and multiplexed over the SAME web bridge as the terminal — each
		// monaco-languageclient gets a (slot, channel) the host routes to its server's stdio. No socket/port/token of
		// its own, so language intelligence inherits the backend's transport (in-process, WebSocket, or a future
		// TLS-proxied one) and reaches remote sessions. The catalog is advertised in LspConfigJson so the page lazily
		// starts a client per language and feeds each server its defaults (e.g. gopls needs {"semanticTokens":true}).
		Lsp = new LspController(bridge, workspaceRoot, new LspServerLauncher(), LanguageServerCatalog.Resolve, Tagged("[lsp]"));
		// Watch the worktree for on-disk edits (Claude's, or external): fan each debounced batch to the editor's
		// file:// provider (FileChanges) AND to the live language servers (didChangeWatchedFiles). Owned here, not by
		// the LSP layer, so it runs even with zero servers connected. Started eagerly.
		_watcher = new WorkspaceWatcher(workspaceRoot, LanguageServerCatalog.WatchedExtensions, OnWatchedChanges, Tagged("[lsp]"), debounceMs: 250);
		_watcher.Start();
	}

	/// <summary>This session's identity within its workspace.</summary>
	public string Id { get; }

	/// <summary>The directory this session's claude, shell, file opener, and LSP are rooted at.</summary>
	public string WorkspaceRoot { get; }

	/// <summary>The session's filesystem, used to persist the editor's autosaved buffers to disk.</summary>
	public IFileSystem FileSystem { get; }

	/// <summary>Serves the editor's host-backed <c>file://</c> provider (workspace-scoped fs-stat/read/write).</summary>
	public FileProviderService FileProvider { get; }

	/// <summary>Owns this workspace's scratch (untitled-buffer) directory; New File creates a file here.</summary>
	public ScratchStore Scratch { get; }

	/// <summary>Lists directories under the session root for the contextual file browser.</summary>
	public WorkspaceBrowser Browser { get; }

	/// <summary>Flat recursive file list under the session root, for the omnibar "Go to File" quick-open.</summary>
	public WorkspaceFileIndex FileIndex { get; }

	/// <summary>The claude TUI terminal.</summary>
	public TerminalController Claude { get; }

	/// <summary>The plain shell terminal.</summary>
	public TerminalController Shell { get; }

	/// <summary>Resolves a clicked <c>file:line</c> and pushes its contents to the editor.</summary>
	public FileOpener FileOpener { get; }

	/// <summary>Renders Claude's <c>openDiff</c> proposals to the Monaco diff view and resolves them.</summary>
	public McpDiffPresenter DiffPresenter { get; }

	/// <summary>
	/// The per-session gate for editor-mutating page messages, active only while this session drives the page; a
	/// muted session holds its show-diff/open-file/close-tab and replays it on switch-in.
	/// </summary>
	public SessionEditorChannel EditorChannel { get; }

	/// <summary>The IDE-MCP + registry servers for this session.</summary>
	public IdeIntegration Ide { get; }

	/// <summary>Routes command invocations (runCommand over MCP, invoke-command from the web) to Core/web handlers.</summary>
	public CommandDispatcher Commands { get; }

	/// <summary>Tracks the editor's active file + selection so claude knows what the user is looking at.</summary>
	public EditorStore Editor { get; }

	/// <summary>
	/// This session's open editor tabs (paths + opaque view state), in memory for the window's lifetime. The page
	/// is the sole writer; the core pushes it as a <c>set-editor-session</c> on a switch so the editor rebinds to
	/// this session's worktree files. The primary also mirrors to the persisted store; worktree sessions don't.
	/// </summary>
	public EditorSession EditorSession { get; set; } = EditorSession.Empty;

	/// <summary>Records every file changed this session (diff vs. each file's session baseline).</summary>
	public SessionChangeTracker Changes { get; }

	/// <summary>Claude's edit mode (default/acceptEdits/plan), observed off the hook stream; Weavie reflects it, never sets it.</summary>
	public ObservedPermissionMode ObservedMode { get; }

	/// <summary>The live status of this session's Claude (Starting/Working/NeedsInput/Idle/Error), for the rail.</summary>
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
				"error", $"Couldn't list {(string.IsNullOrEmpty(requestedPath) ? Browser.Root : requestedPath)}: {ex.Message}"));
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
	/// <c>selection_changed</c> notification to claude over the IDE-MCP connection.
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
			// No longer the foreground editor: drop the active-file/open-tab mirror so this session's Claude
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
		Claude.SlotId = slotId;
		Shell.SlotId = slotId;
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
			Claude.Dispose();
			Shell.Dispose();
		}).ConfigureAwait(false);
		_watcher.Dispose();
		await Ide.DisposeAsync().ConfigureAwait(false);
		await Lsp.DisposeAsync().ConfigureAwait(false);
	}
}
