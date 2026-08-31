using System.IO.Pipes;
using Weavie.Core.Hooks;

namespace Weavie.Hosting.Desktop;

/// <summary>
/// Listens for a second launch handing over the paths the OS gave it. Modelled on <c>HookBridgeServer</c>: a
/// fixed pool of always-listening instances, reused via <c>Disconnect</c> rather than disposed between
/// connections, because disposing a bound instance unlinks the socket file on Unix.
/// </summary>
public sealed class InstanceServer : IAsyncDisposable {
	private const int MaxInstances = 4;

	private readonly string _pipeName;
	private readonly Func<IReadOnlyList<string>, HandoffReply> _handle;
	private readonly Action<string> _log;
	private readonly CancellationTokenSource _cts = new();
	private Task? _acceptLoop;

	/// <summary>Serves handovers on the pipe for <paramref name="weavieRoot"/>.</summary>
	/// <param name="weavieRoot">The Weavie root the pipe name derives from.</param>
	/// <param name="handle">Decides what happens to the handed-over paths; runs off the UI thread.</param>
	/// <param name="log">Diagnostic log sink.</param>
	public InstanceServer(string weavieRoot, Func<IReadOnlyList<string>, HandoffReply> handle, Action<string> log) {
		ArgumentException.ThrowIfNullOrEmpty(weavieRoot);
		ArgumentNullException.ThrowIfNull(handle);
		ArgumentNullException.ThrowIfNull(log);
		_pipeName = InstanceProtocol.PipeName(weavieRoot);
		_handle = handle;
		_log = log;
	}

	/// <summary>Begins accepting handovers. Call once.</summary>
	public void Start() {
		if (_acceptLoop is not null) {
			throw new InvalidOperationException("The instance server is already started.");
		}

		_acceptLoop = Task.WhenAll(
			Enumerable.Range(0, MaxInstances).Select(_ => Task.Run(() => ServeAsync(_cts.Token))));
	}

	private async Task ServeAsync(CancellationToken ct) {
		NamedPipeServerStream? server = null;
		try {
			while (!ct.IsCancellationRequested) {
				if (server is null) {
					try {
						server = new NamedPipeServerStream(
							_pipeName, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte,
							PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
					} catch (IOException ex) {
						_log($"open-with pipe unavailable: {ex.Message}");
						await Task.Delay(250, ct).ConfigureAwait(false);
						continue;
					}
				}

				try {
					await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
					await HandleAsync(server, ct).ConfigureAwait(false);
					server.Disconnect();
				} catch (OperationCanceledException) {
					break;
				} catch (Exception ex) {
					_log($"open-with handover failed: {ex.Message}");
					server.Dispose();
					server = null;
				}
			}
		} finally {
			server?.Dispose();
		}
	}

	private async Task HandleAsync(NamedPipeServerStream server, CancellationToken ct) {
		if (await HookProtocol.ReadFramedAsync(server, ct).ConfigureAwait(false) is not { } payload) {
			return;
		}

		var reply = InstanceProtocol.DecodeRequest(payload) is { } paths
			? _handle(paths)
			: new HandoffReply(false, string.Empty);
		await HookProtocol.WriteFramedAsync(server, InstanceProtocol.EncodeReply(reply), ct).ConfigureAwait(false);
	}

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		await _cts.CancelAsync().ConfigureAwait(false);
		if (_acceptLoop is { } loop) {
			try {
				await loop.ConfigureAwait(false);
			} catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) {
				// Teardown races the accept loop by design.
			}
		}

		_cts.Dispose();
	}
}
