namespace Weavie.Core.Lsp;

/// <summary>
/// A spawned language server you exchange LSP base-protocol messages with: outbound JSON-RPC payloads are
/// written (Content-Length framed) to its stdin in submission order, inbound stdout frames are raised de-framed
/// via <see cref="FrameReceived"/>, and its exit is raised once via <see cref="Exited"/>. The transport that
/// carries these payloads to the editor (the web bridge) is not this type's concern — it deals only in raw
/// JSON-RPC bytes. Obtained from <see cref="ILspServerLauncher"/>; disposing kills and reaps the process.
/// </summary>
public interface ILspServerProcess : IDisposable {
	/// <summary>Raised with each complete JSON-RPC payload read from the server's stdout (UTF-8 bytes, de-framed).</summary>
	event Action<byte[]>? FrameReceived;

	/// <summary>Raised exactly once when the server process exits, with its exit code (0 = clean).</summary>
	event Action<int>? Exited;

	/// <summary>Begins the stdout/stderr read loops and the stdin write pump. Call once, after wiring the events.</summary>
	void Start();

	/// <summary>
	/// Queues one JSON-RPC payload to be written to the server's stdin. Writes are drained by a single pump, so
	/// payloads reach the server in submission order (LSP requires it — e.g. <c>didOpen</c> before a completion).
	/// A no-op once the server has exited.
	/// </summary>
	/// <param name="payload">The raw JSON-RPC payload (UTF-8) to frame and send.</param>
	void Write(ReadOnlyMemory<byte> payload);
}
