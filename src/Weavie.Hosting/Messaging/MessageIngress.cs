using System.Threading.Channels;
using Weavie.Core.Diagnostics;

namespace Weavie.Hosting.Messaging;

internal sealed class MessageIngress : IAsyncDisposable {
	private readonly Channel<IngressItem> _queue = Channel.CreateUnbounded<IngressItem>(
		new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
	private readonly IUiDispatcher _dispatcher;
	private readonly Func<WebPeer, string, Task> _route;
	private readonly Action<WebPeer> _disconnect;
	private readonly DiagnosticWorker _diagnostics;
	private readonly CancellationTokenSource _shutdown = new();
	private readonly Task _pump;
	private int _closed;

	public MessageIngress(
		IUiDispatcher dispatcher,
		Func<WebPeer, string, Task> route,
		Action<WebPeer> disconnect,
		Action<string> log)
		: this(dispatcher, route, disconnect, new DiagnosticWorker(log)) {
	}

	public MessageIngress(
		IUiDispatcher dispatcher,
		Func<WebPeer, string, Task> route,
		Action<WebPeer> disconnect,
		DiagnosticWorker diagnostics) {
		ArgumentNullException.ThrowIfNull(dispatcher);
		ArgumentNullException.ThrowIfNull(route);
		ArgumentNullException.ThrowIfNull(disconnect);
		ArgumentNullException.ThrowIfNull(diagnostics);
		_dispatcher = dispatcher;
		_route = route;
		_disconnect = disconnect;
		_diagnostics = diagnostics;
		_pump = Task.Run(PumpAsync);
	}

	public void Enqueue(WebPeer peer, string json) {
		ArgumentNullException.ThrowIfNull(json);
		TryWrite(new MessageItem(peer, json));
	}

	public void EnqueueDisconnect(WebPeer peer) => TryWrite(new DisconnectItem(peer));

	public async Task ProbeAsync(CancellationToken ct) {
		var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		Write(new ProbeItem(completion));
		await completion.Task.WaitAsync(ct).ConfigureAwait(false);
	}

	public async ValueTask DisposeAsync() {
		if (Interlocked.Exchange(ref _closed, 1) != 0) {
			await _pump.ConfigureAwait(false);
			return;
		}

		_queue.Writer.TryComplete();
		await _shutdown.CancelAsync().ConfigureAwait(false);
		await _pump.ConfigureAwait(false);
		_shutdown.Dispose();
	}

	private void Write(IngressItem item) {
		if (Volatile.Read(ref _closed) != 0 || !_queue.Writer.TryWrite(item)) {
			throw new ObjectDisposedException(nameof(MessageIngress));
		}
	}

	private void TryWrite(IngressItem item) {
		if (Volatile.Read(ref _closed) == 0) {
			_queue.Writer.TryWrite(item);
		}
	}

	private async Task PumpAsync() {
		try {
			await foreach (var item in _queue.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false)) {
				try {
					await _dispatcher.InvokeAsync(
						() => {
							switch (item) {
								case MessageItem message:
									Observe(_route(message.Peer, message.Json));
									break;
								case DisconnectItem disconnect:
									_disconnect(disconnect.Peer);
									break;
								case ProbeItem probe:
									probe.Completion.TrySetResult();
									break;
							}

							return Task.CompletedTask;
						},
						_shutdown.Token).ConfigureAwait(false);
				} catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) {
					Reject(item);
					return;
				} catch (Exception ex) {
					if (item is ProbeItem probe) {
						probe.Completion.TrySetException(ex);
					}

					Report($"[bridge] ingress admission failed: {ex}");
				}
			}
		} catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) {
		} finally {
			while (_queue.Reader.TryRead(out var pending)) {
				Reject(pending);
			}
		}
	}

	private static void Reject(IngressItem item) {
		if (item is ProbeItem probe) {
			probe.Completion.TrySetException(new ObjectDisposedException(nameof(MessageIngress)));
		}
	}

	private void Observe(Task dispatch) =>
		_ = dispatch.ContinueWith(
			task => Report($"[bridge] message dispatch failed: {task.Exception}"),
			CancellationToken.None,
			TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);

	private void Report(string message) => _diagnostics.Report(message);

	private abstract record IngressItem;

	private sealed record MessageItem(WebPeer Peer, string Json) : IngressItem;

	private sealed record DisconnectItem(WebPeer Peer) : IngressItem;

	private sealed record ProbeItem(TaskCompletionSource Completion) : IngressItem;
}
