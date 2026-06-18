using System.Text.Json;
using System.Text.Json.Nodes;
using Weavie.Core.Changes;
using Weavie.Core.Configuration;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Layout;
using Weavie.Core.Lsp;
using Weavie.Core.Mcp;
using Weavie.Core.Workspaces;

namespace Weavie.Win.Hosting;

/// <summary>
/// One Weavie <em>session</em> on the Windows host: the live, workspace-scoped backend a single Claude
/// works in — its two ConPTY terminals (claude + shell), the IDE-MCP server + lock file (the session's
/// private channel back to Weavie, so its openDiff/openFile route here), the LSP bridge rooted at the
/// session cwd, the file opener, and the Monaco diff presenter. A <see cref="WorkspaceWindow"/> owns one
/// (v1) and routes the page's terminal / diff / reveal messages to it; multiple sessions per window come
/// later. Mac sibling: the per-session wiring block in AppDelegate.
/// </summary>
internal sealed class HostSession : IAsyncDisposable {
	private readonly HostBridge _bridge;

	/// <summary>
	/// Builds and starts the session's backend rooted at <paramref name="workspaceRoot"/>: terminals,
	/// the IDE-MCP + registry servers (lock file written, discovery env minted), and the LSP bridge.
	/// <paramref name="pageOrigin"/> is the page's origin, used to pin the LSP WebSocket's allowed origin;
	/// <paramref name="id"/> is this session's identity within its workspace.
	/// </summary>
	public HostSession(
		HostBridge bridge,
		SettingsStore settings,
		LayoutStore layout,
		string workspaceRoot,
		string pageOrigin,
		string id) {
		ArgumentNullException.ThrowIfNull(bridge);
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(layout);
		ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
		ArgumentException.ThrowIfNullOrEmpty(id);

		Id = id;
		WorkspaceRoot = workspaceRoot;
		_bridge = bridge;

		var fileSystem = new LocalFileSystem();
		FileSystem = fileSystem;
		Browser = new WorkspaceBrowser(fileSystem, workspaceRoot);
		FileIndex = new WorkspaceFileIndex(fileSystem, workspaceRoot);
		FileIndex.Log += Tagged("[index]");
		Claude = new TerminalController(bridge, "claude", settings) { Workspace = workspaceRoot };
		Shell = new TerminalController(bridge, "shell", settings) { Workspace = workspaceRoot };
		FileOpener = new FileOpener(bridge, fileSystem, workspaceRoot);
		DiffPresenter = new McpDiffPresenter(bridge, fileSystem, FileOpener);
		// Tracks the editor's active file + selection (fed by the page) so the IDE-MCP server can tell
		// this session's claude what the user is looking at.
		Editor = new EditorStore();

		// IDE-MCP: start the loopback server + lock file, render openDiff to Monaco, and inject the
		// discovery env so this session's claude connects to us (the SOLE edit feed). The same store backs
		// the settings MCP tools, so the user can change settings by talking to claude.
		Ide = new IdeIntegration(new PermissionModeDiffPresenter(DiffPresenter, settings), [workspaceRoot], "weavie", settings, layout, Editor);
		Ide.Server.Log += Tagged("[mcp]");
		if (Ide.RegistryServer is not null) {
			Ide.RegistryServer.Log += Tagged("[registry]");
		}

		Claude.ExtraEnvironment = Ide.EnvironmentVariables;
		// Capability registry: hand the spawned claude an --mcp-config pointing at the registry server
		// so the settings tools reach the model as mcp__weavie__* (the IDE server's tools are filtered).
		Claude.McpConfigPath = Ide.WriteMcpConfigFile();
		// Hook bridge: a --settings file whose hooks route claude's tool calls to our relay. The observed
		// stream is logged here; the session change view consumes the same feed.
		Claude.SettingsFilePath = Ide.WriteSettingsFile();
		Ide.HookBridge.Observed += request => {
			Console.WriteLine($"[hook] {request.Event} {request.ToolName}");
			Console.Out.Flush();
		};
		Ide.HookBridge.Log += Tagged("[hook]");

		// Session change tracking: the same hook stream feeds the tracker (baseline at PreToolUse, new
		// content at PostToolUse). Because hooks fire before the permission check, this records edits in
		// every mode (default/acceptEdits/bypass) — independent of openDiff.
		Changes = new SessionChangeTracker(fileSystem);
		Ide.HookBridge.Observed += Changes.Observe;
		Console.WriteLine($"[weavie] IDE-MCP on 127.0.0.1:{Ide.Port}; registry on 127.0.0.1:{Ide.RegistryPort}; workspace {workspaceRoot}; lock {Ide.LockFilePath}");
		Console.Out.Flush();

		// LSP bridge: a loopback WS↔stdio proxy that spawns language servers (bring-your-own, resolved on
		// PATH) and pipes them to monaco-languageclient in the page. The port, a per-session token, and the
		// workspace root flow to the page via LspConfigJson (the window injects it before navigation);
		// mirrors the IDE-MCP loopback + token posture (bind 127.0.0.1, require the token on the WS upgrade;
		// origin pinned to the app).
		string lspToken = IdeLockFile.NewAuthToken();
		Lsp = new LspBridgeServer(lspToken, workspaceRoot, allowedOrigin: pageOrigin);
		Lsp.Log += Tagged("[lsp]");
		int lspPort = Lsp.Start();
		// Advertise the catalog so the page can lazily start a client per language (on first matching
		// document) and feed each server its default settings as initializationOptions + the answer to
		// workspace/configuration (e.g. gopls needs {"semanticTokens":true}).
		var servers = LanguageServerCatalog.All.Select(d => new {
			id = d.Id,
			languageIds = d.LanguageIds,
			settings = string.IsNullOrEmpty(d.DefaultSettingsJson) ? null : JsonNode.Parse(d.DefaultSettingsJson),
		});
		LspConfigJson = JsonSerializer.Serialize(new { url = $"ws://127.0.0.1:{lspPort}", token = lspToken, workspace = workspaceRoot, servers });
		Console.WriteLine($"[weavie] LSP bridge on 127.0.0.1:{lspPort}; workspace {workspaceRoot}");
		Console.Out.Flush();
	}

	/// <summary>This session's identity within its workspace.</summary>
	public string Id { get; }

	/// <summary>The directory this session's claude, shell, file opener, and LSP are rooted at.</summary>
	public string WorkspaceRoot { get; }

	/// <summary>The session's filesystem, used to persist the editor's autosaved buffers to disk.</summary>
	public IFileSystem FileSystem { get; }

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

	/// <summary>The IDE-MCP + registry servers for this session.</summary>
	public IdeIntegration Ide { get; }

	/// <summary>Tracks the editor's active file + selection so claude knows what the user is looking at.</summary>
	public EditorStore Editor { get; }

	/// <summary>Records every file changed this session (diff vs. each file's session baseline).</summary>
	public SessionChangeTracker Changes { get; }

	/// <summary>The LSP bridge server rooted at this session's cwd.</summary>
	public LspBridgeServer Lsp { get; }

	/// <summary>The <c>window.__WEAVIE_LSP__</c> discovery payload the window injects before navigation.</summary>
	public string LspConfigJson { get; }

	/// <summary>
	/// Lists <paramref name="requestedPath"/> within the session root and pushes a <c>dir-listing</c> reply
	/// to the page (directories first). The file browser calls this on open and on folder expand.
	/// </summary>
	public void ListDirectory(string requestedPath) {
		var entries = Browser.List(requestedPath);
		string json = JsonSerializer.Serialize(new {
			type = "dir-listing",
			path = string.IsNullOrEmpty(requestedPath) ? Browser.Root : requestedPath,
			entries = entries.Select(e => new { name = e.Name, path = e.Path, isDir = e.IsDirectory }),
		});
		_bridge.PostToWeb(json);
	}

	/// <summary>
	/// Applies an <c>active-editor-changed</c> message from the page: updates the editor store, which
	/// pushes a <c>selection_changed</c> notification to claude over the IDE-MCP connection.
	/// </summary>
	public void UpdateActiveEditor(JsonElement message) {
		if (ActiveEditor.TryParse(message, out var editor) && editor is not null) {
			Editor.SetActive(editor);
		}
	}

	private static Action<string> Tagged(string tag) => line => {
		Console.WriteLine($"{tag} {line}");
		Console.Out.Flush();
	};

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		Claude.Dispose();
		Shell.Dispose();
		await Ide.DisposeAsync().ConfigureAwait(false);
		await Lsp.DisposeAsync().ConfigureAwait(false);
	}
}
