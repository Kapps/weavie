using System.IO.Pipes;
using System.Text;

namespace Weavie.Core.Hooks;

/// <summary>
/// In-process listener the hook relay dials. Accepts one length-prefixed hook request per connection, raises
/// <see cref="Observed"/> (the change-recording stream), and replies with the decision (empty = pass-through).
/// Current-user-only — see <see cref="HookProtocol"/>. Lives for the app's lifetime; relays fail open, so a
/// hiccup here never blocks Claude.
/// </summary>
public sealed class HookBridgeServer : IAsyncDisposable {
	private const int MaxInstances = 4;

	private readonly string _pipeName;
	private readonly Func<HookRequest, HookDecision> _decide;
	private readonly CancellationTokenSource _cts = new();
	private Task? _acceptLoop;

	/// <summary>Creates a server that listens on <paramref name="pipeName"/> once <see cref="Start"/> is called.</summary>
	/// <param name="pipeName">The pipe name (see <see cref="HookProtocol.PipeName"/>).</param>
	/// <param name="decide">Maps an observed request to a decision; defaults to pass-through (a pure recorder).</param>
	public HookBridgeServer(string pipeName, Func<HookRequest, HookDecision>? decide) {
		ArgumentException.ThrowIfNullOrEmpty(pipeName);
		_pipeName = pipeName;
		_decide = decide ?? (static _ => HookDecision.PassThrough);
	}

	/// <summary>Raised for every observed hook event (off the UI thread). The change-recording feed.</summary>
	public event Action<HookRequest>? Observed;

	/// <summary>
	/// Raised after <see cref="Observed"/> with the decision the bridge replied (off the UI thread). Lets the
	/// session status tell a PermissionRequest that passes through (a dialog is about to appear — NeedsInput)
	/// from one the gate auto-answered (the turn keeps running).
	/// </summary>
	public event Action<HookRequest, HookDecision>? Decided;

	/// <summary>Diagnostic log lines (pipe errors, observer faults).</summary>
	public event Action<string>? Log;

	/// <summary>Begins accepting relay connections. Call once.</summary>
	public void Start() {
		if (_acceptLoop is not null) {
			throw new InvalidOperationException("Server already started.");
		}
		// A fixed pool of always-listening instances, so a relay connecting always finds a free one. The relay
		// fires Pre/PostToolUse back-to-back as separate one-shot processes; with a single listener that's
		// disposed and recreated per connection, the second connect races the gap — and on Linux (named pipes
		// are Unix sockets) disposing the bound instance unlinks the socket file, resetting that connect
		// ("broken pipe"). Keeping the instances bound for the server's lifetime and reusing them via Disconnect
		// removes both races.
		_acceptLoop = Task.WhenAll(Enumerable.Range(0, MaxInstances).Select(_ => Task.Run(() => ServeAsync(_cts.Token))));
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
						Log?.Invoke($"hook pipe unavailable: {ex.Message}");
						await Task.Delay(250, ct).ConfigureAwait(false);
						continue;
					}
				}

				try {
					await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
					await HandleConnectionAsync(server, ct).ConfigureAwait(false);
					server.Disconnect(); // reuse the bound instance — never dispose mid-life (that unlinks the socket)
				} catch (OperationCanceledException) {
					break;
				} catch (Exception ex) when (ex is IOException or ObjectDisposedException) {
					await server.DisposeAsync().ConfigureAwait(false);
					server = null;
				}
			}
		} finally {
			if (server is not null) {
				await server.DisposeAsync().ConfigureAwait(false);
			}
		}
	}

	private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken ct) {
		byte[]? requestBytes = await HookProtocol.ReadFramedAsync(server, ct).ConfigureAwait(false);
		byte[] response = [];

		if (requestBytes is not null) {
			var request = HookRequest.Parse(Encoding.UTF8.GetString(requestBytes));
			if (request is not null) {
				RaiseObserved(request);
				var decision = _decide(request);
				RaiseDecided(request, decision);
				string? json = decision.ToHookOutputJson(request.Event);
				if (json is not null) {
					response = Encoding.UTF8.GetBytes(json);
				}
			}
		}

		await HookProtocol.WriteFramedAsync(server, response, ct).ConfigureAwait(false);
	}

	private void RaiseObserved(HookRequest request) {
		try {
			Observed?.Invoke(request);
		} catch (Exception ex) {
			Log?.Invoke($"hook observer threw: {ex.Message}");
		}
	}

	private void RaiseDecided(HookRequest request, HookDecision decision) {
		try {
			Decided?.Invoke(request, decision);
		} catch (Exception ex) {
			Log?.Invoke($"hook decision observer threw: {ex.Message}");
		}
	}

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		await _cts.CancelAsync().ConfigureAwait(false);
		if (_acceptLoop is not null) {
			try {
				await _acceptLoop.ConfigureAwait(false);
			} catch (OperationCanceledException) {
				// Expected: the loop was just cancelled.
			}
		}
		_cts.Dispose();
	}
}
