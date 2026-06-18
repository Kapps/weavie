using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Editor;
using Weavie.Core.Hooks;
using Weavie.Core.Layout;
using Weavie.Core.Theming;

namespace Weavie.Core.Mcp;

/// <summary>
/// One-call setup of the Claude-facing MCP surfaces, with the same per-session token:
/// <list type="bullet">
/// <item>the <b>IDE server</b> (discovered via the <c>~/.claude/ide/&lt;port&gt;.lock</c> file) carries
/// the harness RPC tools — openDiff/openFile/... Claude Code filters these before they reach the model,
/// so they're for the CLI's own UI, not user-facing.</item>
/// <item>the <b>registry server</b> (advertised to the spawned <c>claude</c> via a generated
/// <c>--mcp-config</c>) carries the capability tools — <c>listSettings</c>/<c>getSetting</c>/
/// <c>setSetting</c> — which DO reach the model as <c>mcp__weavie__*</c>, so the user can drive Weavie
/// by talking to Claude. Created only when a settings store is supplied.</item>
/// </list>
/// Disposal removes the lock file and stops both servers. See <c>docs/concepts/mcp-registry.md</c>.
/// </summary>
public sealed class IdeIntegration : IAsyncDisposable {
	private const string McpServerName = "weavie";

	/// <summary>
	/// Mints an auth token, starts the IDE server (writing the lock file) and—when
	/// <paramref name="settings"/> is supplied—the capability registry server, both on ephemeral
	/// loopback ports with the same token.
	/// </summary>
	public IdeIntegration(
		IDiffPresenter presenter,
		IReadOnlyList<string> workspaceFolders,
		string ideName = "weavie",
		SettingsStore? settings = null,
		LayoutStore? layout = null,
		EditorStore? editor = null,
		CommandDispatcher? commands = null,
		KeybindingStore? keybindings = null,
		ThemeOverridesStore? themeOverrides = null,
		Func<HookRequest, string?>? editLocator = null) {
		ArgumentNullException.ThrowIfNull(workspaceFolders);

		AuthToken = IdeLockFile.NewAuthToken();
		// The IDE server (not the registry) carries the active-editor context: getCurrentSelection/
		// getOpenEditors + the pushed selection_changed notification all live on the IDE connection.
		Server = new McpServer(AuthToken, presenter, workspaceFolders, ideName, editor: editor);
		Port = Server.Start();
		IdeLockFile.Write(Port, workspaceFolders, ideName, AuthToken);

		// The hook bridge: a current-user-only pipe (no token) the spawned claude's PreToolUse/PostToolUse
		// hooks dial via the relay, scoped to this instance by the IDE port. Carries the change-recording
		// stream + the permission gate (bypassPermissions → auto-allow), read live from the settings store.
		HookBridge = new HookBridgeServer(
			HookProtocol.PipeName(Port),
			request => {
				var decision = HookPolicy.Decide(request, settings?.GetString("claude.permissionMode") ?? "default");
				// On a landed edit, attach a clickable file:line jump target as the hook's systemMessage so
				// Claude prints it in the TUI (the terminal turns path:line tokens into Monaco reveals).
				string? location = editLocator?.Invoke(request);
				return location is null ? decision : decision with { SystemMessage = location };
			});
		HookBridge.Start();

		if (settings is not null) {
			RegistryServer = new McpServer(
				AuthToken, presenter, workspaceFolders, ideName, settings, registryMode: true, layout: layout,
				commands: commands, keybindings: keybindings, themeOverrides: themeOverrides);
			RegistryPort = RegistryServer.Start();
		}
	}

	/// <summary>The running IDE MCP server (harness RPC: openDiff, etc.).</summary>
	public McpServer Server { get; }

	/// <summary>The loopback port the IDE server is listening on.</summary>
	public int Port { get; }

	/// <summary>The capability registry MCP server (settings tools), or <c>null</c> if no store was supplied.</summary>
	public McpServer? RegistryServer { get; }

	/// <summary>The loopback port the registry server is listening on; 0 when there is none.</summary>
	public int RegistryPort { get; }

	/// <summary>The auth token Claude must present, also written into the lock file and the MCP config.</summary>
	public string AuthToken { get; }

	/// <summary>The hook bridge listening for the spawned claude's PreToolUse/PostToolUse relay connections.</summary>
	public HookBridgeServer HookBridge { get; }

	/// <summary>Path of the lock file written for the current <see cref="Port"/>.</summary>
	public string LockFilePath => IdeLockFile.PathForPort(Port);

	/// <summary>Env vars to inject into the spawned <c>claude</c> so it connects here.</summary>
	public IReadOnlyDictionary<string, string> EnvironmentVariables => new Dictionary<string, string>(StringComparer.Ordinal) {
		["CLAUDE_CODE_SSE_PORT"] = Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
		["ENABLE_IDE_INTEGRATION"] = "true",
		// Names the hook pipe for the relay (claude's hook child inherits this). Disk-less, non-secret —
		// the pipe's auth is its current-user-only ACL, not this value.
		[HookProtocol.PipeEnvVar] = HookProtocol.PipeName(Port),
	};

	/// <summary>
	/// Writes a Claude Code MCP config file pointing at the registry server (ws + Bearer token) and
	/// returns its path, for the spawned <c>claude</c>'s <c>--mcp-config</c>. Returns <c>null</c> when
	/// there is no registry server. The file is rewritten each run with the current ephemeral port.
	/// </summary>
	public string? WriteMcpConfigFile() {
		if (RegistryServer is null) {
			return null;
		}

		string directory = WeaviePaths.Internal("mcp");
		Directory.CreateDirectory(directory);
		// Port-scoped filename so concurrent weavie instances never clobber each other's config (each
		// spawned claude is handed the exact path for its own registry server's ephemeral port).
		string path = Path.Combine(directory, $"weavie-{RegistryPort}.mcp.json");
		string json =
			$"{{\"mcpServers\":{{\"{McpServerName}\":{{\"type\":\"ws\",\"url\":\"ws://127.0.0.1:{RegistryPort}\"," +
			$"\"headers\":{{\"Authorization\":\"Bearer {AuthToken}\"}}}}}}}}";
		File.WriteAllText(path, json);
		return path;
	}

	/// <summary>
	/// Writes a Claude Code settings file (a <c>hooks</c> block only) for the spawned claude's
	/// <c>--settings</c>, routing PreToolUse/PostToolUse for mutating tools to this instance's hook relay,
	/// and returns its path. Port-scoped filename, like the MCP config. Returns <c>null</c> if the host
	/// executable path is unknown (no relay to point at).
	/// </summary>
	public string? WriteSettingsFile() {
		string? host = Environment.ProcessPath;
		if (string.IsNullOrEmpty(host)) {
			return null;
		}

		string directory = WeaviePaths.Internal("hooks");
		Directory.CreateDirectory(directory);
		string path = Path.Combine(directory, $"weavie-{Port}.settings.json");
		File.WriteAllText(path, HookSettings.BuildJson(host));
		return path;
	}

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		IdeLockFile.Delete(Port);
		await HookBridge.DisposeAsync().ConfigureAwait(false);
		await Server.DisposeAsync().ConfigureAwait(false);
		if (RegistryServer is not null) {
			await RegistryServer.DisposeAsync().ConfigureAwait(false);
		}
	}
}
