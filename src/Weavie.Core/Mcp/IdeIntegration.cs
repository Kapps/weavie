using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Hooks;
using Weavie.Core.Layout;
using Weavie.Core.Theming;

namespace Weavie.Core.Mcp;

/// <summary>
/// One-call setup of the Claude-facing MCP surfaces, sharing one per-session token:
/// <list type="bullet">
/// <item>the <b>IDE server</b> (discovered via <c>~/.claude/ide/&lt;port&gt;.lock</c>) carries the harness RPC
/// tools (openDiff/openFile/...) that Claude Code filters before they reach the model.</item>
/// <item>the <b>registry server</b> (advertised via a generated <c>--mcp-config</c>) carries the capability
/// tools that DO reach the model as <c>mcp__weavie__*</c>. Created only when a settings store is supplied.</item>
/// </list>
/// Disposal removes the lock file and stops both servers. See <c>docs/concepts/mcp-registry.md</c>.
/// </summary>
public sealed class IdeIntegration : IAsyncDisposable {
	private const string McpServerName = "weavie";

	/// <summary>
	/// Mints an auth token and starts the IDE server (writing the lock file) plus, when
	/// <paramref name="settings"/> is supplied, the registry server — both on loopback ports with the same token.
	/// </summary>
	public IdeIntegration(
		IDiffPresenter presenter,
		IReadOnlyList<string> workspaceFolders,
		string ideName,
		SettingsStore? settings,
		LayoutStore? layout,
		EditorStore? editor,
		CommandDispatcher? commands,
		KeybindingStore? keybindings,
		ThemeOverridesStore? themeOverrides,
		Func<HookRequest, string?>? editLocator,
		Func<string> currentSessionId) {
		ArgumentNullException.ThrowIfNull(workspaceFolders);
		ArgumentNullException.ThrowIfNull(currentSessionId);

		AuthToken = IdeLockFile.NewAuthToken();
		// The IDE server (not the registry) carries the active-editor context (getCurrentSelection/
		// getOpenEditors + the pushed selection_changed notification).
		Server = new McpServer(
			AuthToken, presenter, workspaceFolders, ideName, settings: null, registryMode: false, layout: null,
			editor: editor, commands: null, keybindings: null, themeOverrides: null, currentSessionId: null);
		Port = Server.Start();
		IdeLockFile.Write(Port, workspaceFolders, ideName, AuthToken);

		// The hook bridge: a current-user-only pipe (no token), scoped to this instance by the IDE port, carrying
		// the change-recording stream + the tool-permission gate (claude.allowAllTools auto-allows non-edit tools).
		HookBridge = new HookBridgeServer(
			HookProtocol.PipeName(Port),
			request => {
				var decision = HookPolicy.Decide(request, settings?.GetBool("claude.allowAllTools", fallback: false) ?? false);
				// On a landed edit, attach a clickable file:line jump target as systemMessage (the TUI turns
				// path:line tokens into Monaco reveals).
				string? location = editLocator?.Invoke(request);
				return location is null ? decision : decision with { SystemMessage = location };
			});
		HookBridge.Start();

		if (settings is not null) {
			RegistryServer = new McpServer(
				AuthToken, presenter, workspaceFolders, ideName, settings, registryMode: true, layout: layout,
				editor: null, commands: commands, keybindings: keybindings, themeOverrides: themeOverrides,
				currentSessionId: currentSessionId);
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
		// Names the hook pipe for the relay (inherited by claude's hook child). Non-secret: auth is the pipe's
		// current-user-only ACL, not this value.
		[HookProtocol.PipeEnvVar] = HookProtocol.PipeName(Port),
	};

	/// <summary>
	/// Writes the <c>--mcp-config</c> file pointing at the registry server (ws + Bearer token) and returns its
	/// path; <c>null</c> when there is no registry server.
	/// </summary>
	public string? WriteMcpConfigFile() {
		if (RegistryServer is null) {
			return null;
		}

		// The config embeds the Bearer token, so keep it (and the dir) owner-only on POSIX.
		string directory = WeaviePaths.Internal("mcp");
		SecureFile.CreateDirectory(directory);
		// Port-scoped filename so concurrent weavie instances never clobber each other's config.
		string path = Path.Combine(directory, $"weavie-{RegistryPort}.mcp.json");
		string json =
			$"{{\"mcpServers\":{{\"{McpServerName}\":{{\"type\":\"ws\",\"url\":\"ws://127.0.0.1:{RegistryPort}\"," +
			$"\"headers\":{{\"Authorization\":\"Bearer {AuthToken}\"}}}}}}}}";
		SecureFile.WriteAllText(path, json);
		return path;
	}

	/// <summary>
	/// Writes the spawned claude's <c>--settings</c> file (a <c>hooks</c> block routing the permission gate +
	/// change-tracking hooks to the co-located relay) and returns its path. Throws if the relay is missing —
	/// no fallback, so a build that failed to co-locate it surfaces loudly.
	/// </summary>
	public string WriteSettingsFile() {
		string relay = ResolveRelayBinary()
			?? throw new InvalidOperationException(
				$"Hook relay '{RelayBinaryName}' was not found next to the app at '{AppContext.BaseDirectory}'. "
				+ "The build co-locates it (see HookRelay.targets); a Release build requires the NativeAOT C++ toolchain.");

		string directory = WeaviePaths.Internal("hooks");
		Directory.CreateDirectory(directory);
		string path = Path.Combine(directory, $"weavie-{Port}.settings.json");
		File.WriteAllText(path, HookSettings.BuildJson(relay));
		return path;
	}

	/// <summary>The standalone hook-relay executable's filename for this platform.</summary>
	private static string RelayBinaryName => OperatingSystem.IsWindows() ? "weavie-hook-relay.exe" : "weavie-hook-relay";

	/// <summary>
	/// The hook-relay executable co-located with the app (resolved against <see cref="AppContext.BaseDirectory"/>),
	/// or <see langword="null"/> when absent. No fallback; the caller fails loudly on null.
	/// </summary>
	private static string? ResolveRelayBinary() {
		string candidate = Path.Combine(AppContext.BaseDirectory, RelayBinaryName);
		return File.Exists(candidate) ? candidate : null;
	}

	/// <summary>
	/// Writes the <c>--append-system-prompt-file</c> appendix (<see cref="EmbeddedClaudeGuidance"/>) and returns
	/// its path; <c>null</c> without a registry server, since the appendix points at <c>mcp__weavie__*</c> tools.
	/// </summary>
	public string? WriteSystemPromptFile() {
		if (RegistryServer is null) {
			return null;
		}

		string directory = WeaviePaths.Internal("mcp");
		Directory.CreateDirectory(directory);
		string path = Path.Combine(directory, $"weavie-{RegistryPort}.system-prompt.txt");
		File.WriteAllText(path, EmbeddedClaudeGuidance.SystemPromptAppendix);
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
