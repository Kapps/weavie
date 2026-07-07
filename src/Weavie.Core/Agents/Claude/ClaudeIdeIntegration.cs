using Weavie.Core.Configuration;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Hooks;
using Weavie.Core.Mcp;

namespace Weavie.Core.Agents.Claude;

/// <summary>Claude-only IDE discovery, hook bridge, generated launch files, and decision serialization.</summary>
public sealed class ClaudeIdeIntegration : IAsyncDisposable {
	private const string McpServerName = "weavie";
	private readonly CapabilityRegistryHost _registry;
	private readonly HostRuntimeInfo _runtime;

	/// <summary>Starts Claude's IDE server and hook bridge against the shared session credential.</summary>
	public ClaudeIdeIntegration(
		CapabilityRegistryHost registry,
		IDiffPresenter presenter,
		IReadOnlyList<string> workspaceFolders,
		string ideName,
		SettingsStore settings,
		EditorStore editor,
		HostRuntimeInfo runtime,
		Func<HookRequest, AgentEventFeedback> observe,
		Action<HookRequest, HookDecision> decided) {
		ArgumentNullException.ThrowIfNull(registry);
		ArgumentNullException.ThrowIfNull(presenter);
		ArgumentNullException.ThrowIfNull(workspaceFolders);
		ArgumentException.ThrowIfNullOrEmpty(ideName);
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(editor);
		ArgumentNullException.ThrowIfNull(runtime);
		ArgumentNullException.ThrowIfNull(observe);
		ArgumentNullException.ThrowIfNull(decided);
		_registry = registry;
		_runtime = runtime;
		Server = new McpServer(
			registry.Credential.Token, presenter, workspaceFolders, ideName, settings: null, registryMode: false,
			exposeIdeTools: true, layout: null, editor, commands: null, keybindings: null,
			themeOverrides: null, currentSessionId: null);
		Port = Server.Start();
		IdeLockFile.Write(Port, workspaceFolders, ideName, registry.Credential.Token);
		HookBridge = new HookBridgeServer(
			HookProtocol.PipeName(Port),
			request => {
				var feedback = observe(request);
				var decision = HookPolicy.Decide(
					request, settings.GetBool("claude.allowAllTools", fallback: false));
				string? message = feedback.Messages.FirstOrDefault();
				return message is null ? decision : decision with { SystemMessage = message };
			});
		HookBridge.Decided += decided;
		HookBridge.Start();
	}

	/// <summary>Claude's IDE MCP server.</summary>
	public McpServer Server { get; }

	/// <summary>The IDE server loopback port.</summary>
	public int Port { get; }

	/// <summary>The Claude hook bridge.</summary>
	public HookBridgeServer HookBridge { get; }

	/// <summary>The IDE discovery lock file.</summary>
	public string LockFilePath => IdeLockFile.PathForPort(Port);

	/// <summary>Environment variables injected into Claude for IDE and hook discovery.</summary>
	public IReadOnlyDictionary<string, string> EnvironmentVariables =>
		new Dictionary<string, string>(StringComparer.Ordinal) {
			["CLAUDE_CODE_SSE_PORT"] = Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
			["ENABLE_IDE_INTEGRATION"] = "true",
			[HookProtocol.PipeEnvVar] = HookProtocol.PipeName(Port),
		};

	/// <summary>Writes Claude's registry MCP configuration and returns its path.</summary>
	public string WriteMcpConfigFile() {
		string directory = WeaviePaths.Internal("mcp");
		SecureFile.CreateDirectory(directory);
		string path = Path.Combine(directory, $"weavie-{_registry.Port}.mcp.json");
		string json =
			$"{{\"mcpServers\":{{\"{McpServerName}\":{{\"type\":\"ws\",\"url\":\"ws://127.0.0.1:{_registry.Port}\"," +
			$"\"headers\":{{\"Authorization\":\"Bearer {_registry.Credential.Token}\"}}}}}}}}";
		SecureFile.WriteAllText(path, json);
		return path;
	}

	/// <summary>Writes Claude hook settings and returns their path.</summary>
	public string WriteSettingsFile() {
		string relay = RelayBinaryPath();
		if (!File.Exists(relay)) {
			throw new InvalidOperationException(
				$"Hook relay '{RelayBinaryName}' was not found at '{relay}'. "
				+ "The build co-locates it (see HookRelay.targets); a Release build requires the NativeAOT C++ toolchain.");
		}

		string directory = WeaviePaths.Internal("hooks");
		Directory.CreateDirectory(directory);
		string path = Path.Combine(directory, $"weavie-{Port}.settings.json");
		File.WriteAllText(path, HookSettings.BuildJson(relay));
		return path;
	}

	/// <summary>Writes Claude's Weavie orientation appendix and returns its path.</summary>
	public string WriteSystemPromptFile() {
		string directory = WeaviePaths.Internal("mcp");
		Directory.CreateDirectory(directory);
		string path = Path.Combine(directory, $"weavie-{_registry.Port}.system-prompt.txt");
		File.WriteAllText(path, EmbeddedClaudeGuidance.Compose(_runtime));
		return path;
	}

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		IdeLockFile.Delete(Port);
		await HookBridge.DisposeAsync().ConfigureAwait(false);
		await Server.DisposeAsync().ConfigureAwait(false);
	}

	private static string RelayBinaryName => OperatingSystem.IsWindows() ? "weavie-hook-relay.exe" : "weavie-hook-relay";

	private static string RelayBinaryPath() =>
		ManagedRunnerLayout.CurrentRelayPath(AppContext.BaseDirectory, RelayBinaryName)
		?? Path.Combine(AppContext.BaseDirectory, RelayBinaryName);
}
