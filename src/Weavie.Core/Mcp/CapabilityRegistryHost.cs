using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Editor;
using Weavie.Core.Layout;
using Weavie.Core.Theming;

namespace Weavie.Core.Mcp;

/// <summary>The provider-neutral, model-facing Weavie capability registry for one loaded session.</summary>
public sealed class CapabilityRegistryHost : IAsyncDisposable {
	/// <summary>Starts the standard MCP capability server with <paramref name="credential"/>.</summary>
	public CapabilityRegistryHost(
		AgentSessionCredential credential,
		IDiffPresenter presenter,
		IReadOnlyList<string> workspaceFolders,
		string ideName,
		SettingsStore settings,
		LayoutStore layout,
		EditorStore editor,
		bool exposeIdeTools,
		CommandDispatcher commands,
		KeybindingStore keybindings,
		ThemeOverridesStore themeOverrides,
		Func<string> currentSessionId) {
		ArgumentNullException.ThrowIfNull(credential);
		ArgumentNullException.ThrowIfNull(presenter);
		ArgumentNullException.ThrowIfNull(workspaceFolders);
		ArgumentException.ThrowIfNullOrEmpty(ideName);
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(layout);
		ArgumentNullException.ThrowIfNull(editor);
		ArgumentNullException.ThrowIfNull(commands);
		ArgumentNullException.ThrowIfNull(keybindings);
		ArgumentNullException.ThrowIfNull(themeOverrides);
		ArgumentNullException.ThrowIfNull(currentSessionId);
		Credential = credential;
		Server = new McpServer(
			credential.Token, presenter, workspaceFolders, ideName, settings, registryMode: true, exposeIdeTools,
			layout, editor, commands, keybindings, themeOverrides, currentSessionId);
		Port = Server.Start();
	}

	/// <summary>The shared session credential.</summary>
	public AgentSessionCredential Credential { get; }

	/// <summary>The registry MCP server.</summary>
	public McpServer Server { get; }

	/// <summary>The loopback registry port.</summary>
	public int Port { get; }

	/// <summary>The Streamable HTTP MCP endpoint URL for Codex.</summary>
	public string StreamableHttpUrl => $"http://127.0.0.1:{Port}/mcp";

	/// <inheritdoc/>
	public ValueTask DisposeAsync() => Server.DisposeAsync();
}
