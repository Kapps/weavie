using Weavie.Core.Agents;
using Weavie.Workflows;

namespace Weavie.Core.Mcp;

/// <summary>The provider-neutral, model-facing Weavie capability registry for one loaded session.</summary>
public sealed class CapabilityRegistryHost : IAsyncDisposable {
	/// <summary>
	/// Starts the standard MCP capability server with <paramref name="credential"/>; a session's registry offers
	/// every store in <paramref name="capabilities"/>.
	/// </summary>
	public CapabilityRegistryHost(
		AgentSessionCredential credential,
		IDiffPresenter presenter,
		IReadOnlyList<string> workspaceFolders,
		string ideName,
		McpCapabilities capabilities) {
		ArgumentNullException.ThrowIfNull(credential);
		ArgumentException.ThrowIfNullOrEmpty(ideName);
		ArgumentNullException.ThrowIfNull(capabilities);
		ArgumentException.ThrowIfNullOrEmpty(capabilities.SettingsWorkspaceRoot);
		ArgumentNullException.ThrowIfNull(capabilities.Layout);
		ArgumentNullException.ThrowIfNull(capabilities.Editor);
		ArgumentNullException.ThrowIfNull(capabilities.Commands);
		ArgumentNullException.ThrowIfNull(capabilities.Keybindings);
		ArgumentNullException.ThrowIfNull(capabilities.ThemeOverrides);
		Credential = credential;
		Server = new McpServer(credential.Token, presenter, workspaceFolders, ideName, registryMode: true, capabilities);
		Port = Server.Start();
	}

	/// <summary>The shared session credential.</summary>
	public AgentSessionCredential Credential { get; }

	/// <summary>The registry MCP server.</summary>
	public McpServer Server { get; }

	/// <summary>The loopback registry port.</summary>
	public int Port { get; }

	/// <summary>The Streamable HTTP MCP endpoint URL for native agents.</summary>
	public string StreamableHttpUrl => $"http://127.0.0.1:{Port}/mcp";

	/// <summary>Registers a conversation-specific credential and stage admission.</summary>
	public CapabilityRegistryGrant CreateGrant(Func<WorkflowStageCall?> stageCall) => Server.CreateGrant(stageCall);

	/// <inheritdoc cref="McpServer.CreateSideGrant"/>
	public CapabilityRegistryGrant CreateSideGrant(string conversationId) => Server.CreateSideGrant(conversationId);

	/// <inheritdoc/>
	public ValueTask DisposeAsync() => Server.DisposeAsync();
}
