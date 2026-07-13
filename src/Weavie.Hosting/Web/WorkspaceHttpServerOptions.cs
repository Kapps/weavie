using System.Security.Cryptography;

namespace Weavie.Hosting.Web;

/// <summary>The binding, authentication, assets, and control-plane shape of a workspace HTTP server.</summary>
public sealed record WorkspaceHttpServerOptions(
	string BindAddress,
	int Port,
	string Token,
	string WebRoot,
	bool EnableControl) {
	/// <summary>Creates a token-gated, loopback-only server on an OS-assigned port for a native workspace.</summary>
	public static WorkspaceHttpServerOptions Native(string webRoot) => Loopback(0, webRoot);

	/// <summary>Creates a token-gated, loopback-only server on <paramref name="port"/>.</summary>
	public static WorkspaceHttpServerOptions Loopback(int port, string webRoot) =>
		new("127.0.0.1", port, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(), webRoot, false);
}

/// <summary>A WebSocket control-plane endpoint hosted on the shared workspace server.</summary>
public interface IWorkspaceWebSocketBridge {
	/// <summary>Whether this host carries its bridge over the workspace HTTP server.</summary>
	bool Available { get; }

	/// <summary>Serves one accepted WebSocket until it disconnects or the server stops.</summary>
	Task ServeAsync(System.Net.WebSockets.WebSocket socket, CancellationToken cancellationToken);
}

/// <summary>The endpoint used by native hosts, whose control plane stays on their in-process WebView bridge.</summary>
public sealed class UnavailableWorkspaceWebSocketBridge : IWorkspaceWebSocketBridge {
	private UnavailableWorkspaceWebSocketBridge() {
	}

	/// <summary>The shared singleton.</summary>
	public static UnavailableWorkspaceWebSocketBridge Instance { get; } = new();

	/// <inheritdoc/>
	public bool Available => false;

	/// <inheritdoc/>
	public Task ServeAsync(System.Net.WebSockets.WebSocket socket, CancellationToken cancellationToken) =>
		throw new InvalidOperationException("This host does not expose an HTTP WebSocket bridge.");
}
