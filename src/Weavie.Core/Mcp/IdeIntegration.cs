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
		string ideName,
		SettingsStore? settings,
		LayoutStore? layout,
		EditorStore? editor,
		CommandDispatcher? commands,
		KeybindingStore? keybindings,
		ThemeOverridesStore? themeOverrides,
		Func<HookRequest, string?>? editLocator) {
		ArgumentNullException.ThrowIfNull(workspaceFolders);

		AuthToken = IdeLockFile.NewAuthToken();
		// The IDE server (not the registry) carries the active-editor context: getCurrentSelection/
		// getOpenEditors + the pushed selection_changed notification all live on the IDE connection.
		Server = new McpServer(
			AuthToken, presenter, workspaceFolders, ideName, settings: null, registryMode: false, layout: null,
			editor: editor, commands: null, keybindings: null, themeOverrides: null);
		Port = Server.Start();
		IdeLockFile.Write(Port, workspaceFolders, ideName, AuthToken);

		// The hook bridge: a current-user-only pipe (no token) the spawned claude's PreToolUse/PostToolUse
		// hooks dial via the relay, scoped to this instance by the IDE port. Carries the change-recording
		// stream + Weavie's tool-permission gate (claude.allowAllTools → auto-allow non-edit tools), read live
		// from the settings store; edits stay with Claude's own mode, which Weavie only observes.
		HookBridge = new HookBridgeServer(
			HookProtocol.PipeName(Port),
			request => {
				var decision = HookPolicy.Decide(request, settings?.GetBool("claude.allowAllTools", fallback: false) ?? false);
				// On a landed edit, attach a clickable file:line jump target as the hook's systemMessage so
				// Claude prints it in the TUI (the terminal turns path:line tokens into Monaco reveals).
				string? location = editLocator?.Invoke(request);
				return location is null ? decision : decision with { SystemMessage = location };
			});
		HookBridge.Start();

		if (settings is not null) {
			RegistryServer = new McpServer(
				AuthToken, presenter, workspaceFolders, ideName, settings, registryMode: true, layout: layout,
				editor: null, commands: commands, keybindings: keybindings, themeOverrides: themeOverrides);
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
	/// <c>--settings</c>, routing the permission gate (PermissionRequest) + the change-tracking hooks to the
	/// standalone relay binary co-located with the app, and returns its path. Port-scoped filename, like the
	/// MCP config. Throws when the relay is missing: there is no fallback, so a build that failed to
	/// co-locate it surfaces loudly rather than silently degrading.
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
	/// The standalone hook-relay executable co-located with the app by the build, or <see langword="null"/>
	/// when absent. Resolved against <see cref="AppContext.BaseDirectory"/> so it stays correct wherever the
	/// app is installed. There is intentionally no host-as-relay fallback; the caller fails loudly on null.
	/// </summary>
	private static string? ResolveRelayBinary() {
		string candidate = Path.Combine(AppContext.BaseDirectory, RelayBinaryName);
		return File.Exists(candidate) ? candidate : null;
	}

	/// <summary>
	/// Writes the embedded-claude system-prompt appendix (<see cref="EmbeddedClaudeGuidance"/>) for the
	/// spawned claude's <c>--append-system-prompt-file</c>, and returns its path. Port-scoped filename, like
	/// the MCP config. Returns <c>null</c> when there is no registry server — the appendix points claude at
	/// the <c>mcp__weavie__*</c> tools, which only exist when that server is wired.
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
