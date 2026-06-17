using System.IO.Pipes;
using System.Text;

namespace Weavie.Core.Hooks;

/// <summary>
/// The in-process listener the hook relay dials. Accepts one length-prefixed hook request per connection,
/// raises <see cref="Observed"/> (the change-recording stream), and replies with the <see cref="HookPolicy"/>
/// decision (empty = pass-through). Loopback and current-user-only — see <see cref="HookProtocol"/>. Lives for
/// the app's lifetime; the relay processes are transient (one per tool call) and fail open, so a hiccup here
/// never blocks Claude.
/// </summary>
public sealed class HookBridgeServer : IAsyncDisposable {
	private const int MaxInstances = 4;

	private readonly string _pipeName;
	private readonly CancellationTokenSource _cts = new();
	private Task? _acceptLoop;

	/// <summary>Creates a server that listens on <paramref name="pipeName"/> once <see cref="Start"/> is called.</summary>
	/// <param name="pipeName">The pipe name (see <see cref="HookProtocol.PipeName"/>).</param>
	public HookBridgeServer(string pipeName) {
		ArgumentException.ThrowIfNullOrEmpty(pipeName);
		_pipeName = pipeName;
	}

	/// <summary>Raised for every observed hook event (off the UI thread). The change-recording feed.</summary>
	public event Action<HookRequest>? Observed;

	/// <summary>Diagnostic log lines (pipe errors, observer faults).</summary>
	public event Action<string>? Log;

	/// <summary>Begins accepting relay connections. Call once.</summary>
	public void Start() {
		if (_acceptLoop is not null) {
			throw new InvalidOperationException("Server already started.");
		}
		_acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
	}

	private async Task AcceptLoopAsync(CancellationToken ct) {
		while (!ct.IsCancellationRequested) {
			NamedPipeServerStream server;
			try {
				server = new NamedPipeServerStream(
					_pipeName, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte,
					PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
			} catch (IOException ex) {
				Log?.Invoke($"hook pipe unavailable: {ex.Message}");
				try {
					await Task.Delay(250, ct).ConfigureAwait(false);
				} catch (OperationCanceledException) {
					break;
				}
				continue;
			}

			try {
				await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
			} catch (OperationCanceledException) {
				await server.DisposeAsync().ConfigureAwait(false);
				break;
			} catch (Exception ex) when (ex is IOException or ObjectDisposedException) {
				await server.DisposeAsync().ConfigureAwait(false);
				continue;
			}

			await HandleConnectionAsync(server, ct).ConfigureAwait(false);
		}
	}

	private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken ct) {
		try {
			byte[]? requestBytes = await HookProtocol.ReadFramedAsync(server, ct).ConfigureAwait(false);
			byte[] response = [];

			if (requestBytes is not null) {
				var request = HookRequest.Parse(Encoding.UTF8.GetString(requestBytes));
				if (request is not null) {
					RaiseObserved(request);
					string? json = HookPolicy.Decide(request).ToHookOutputJson(request.Event);
					if (json is not null) {
						response = Encoding.UTF8.GetBytes(json);
					}
				}
			}

			await HookProtocol.WriteFramedAsync(server, response, ct).ConfigureAwait(false);
		} catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) {
			// Relay vanished or we're shutting down — the relay fails open, so there's nothing to recover.
		} finally {
			await server.DisposeAsync().ConfigureAwait(false);
		}
	}

	private void RaiseObserved(HookRequest request) {
		try {
			Observed?.Invoke(request);
		} catch (Exception ex) {
			Log?.Invoke($"hook observer threw: {ex.Message}");
		}
	}

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		await _cts.CancelAsync().ConfigureAwait(false);
		if (_acceptLoop is not null) {
			try {
				await _acceptLoop.ConfigureAwait(false);
			} catch (OperationCanceledException) {
			}
		}
		_cts.Dispose();
	}
}
