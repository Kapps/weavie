using System.Text.Json;

namespace Weavie.Hosting.Messaging;

internal partial class MessageBus {
	private async Task<DispatchCompletion> RunHandlerAsync(
		MessagePeer owner,
		WebPeer peer,
		MessageEnvelope envelope,
		HandlerRegistration registration,
		Task admitted,
		MessageOperation operation) {
		CancellationTokenSource? request = null;
		bool requestRegistered = false;
		try {
			if (!Accepting) {
				throw new OperationCanceledException("The endpoint is closing.");
			}

			request = CancellationTokenSource.CreateLinkedTokenSource(
				_dispatchCancellation.Token,
				operation.TimeoutToken);
			if (envelope.RequestId is { } requestId
				&& !_requests.TryAdd(
					(peer, requestId),
					new InboundRequest(envelope.Feature, envelope.Name, request))) {
				LogDiagnostic($"[bridge] rejected duplicate request '{requestId}' from peer '{peer.Id}'");
				return new DispatchCompletion(null);
			}
			requestRegistered = envelope.RequestId is not null;

			var response = await registration
				.InvokeAsync(owner, envelope.Payload, request.Token, admitted, _handlerExecutor, operation)
				.ConfigureAwait(false);
			if (envelope.Kind == MessageKind.Request && operation.TrySettleResponse()) {
				TrySendResponse(peer, envelope, response.Payload, null);
			}
			return new DispatchCompletion(response.AfterResponse);
		} catch (MessageOperationTimeoutException) {
			return new DispatchCompletion(null);
		} catch (OperationCanceledException) when (
			request?.IsCancellationRequested == true
			|| !Accepting) {
			if (envelope.Kind == MessageKind.Request && operation.TrySettleResponse()) {
				TrySendResponse(
					peer,
					envelope,
					default,
					operation.HasTimedOut ? operation.TimeoutDetail() : "The request was cancelled.");
			}
			return new DispatchCompletion(null);
		} catch (Exception ex) {
			if (envelope.Kind == MessageKind.Request && operation.TrySettleResponse()) {
				TrySendResponse(peer, envelope, default, ex.Message);
			} else {
				LogDiagnostic($"[bridge] endpoint event {envelope.Feature}.{envelope.Name} failed: {ex}");
			}
			return new DispatchCompletion(null);
		} finally {
			if (requestRegistered && envelope.RequestId is { } requestId) {
				_requests.TryRemove((peer, requestId), out _);
			}

			request?.Dispose();
		}
	}

	private FeatureLane GetFeatureLane(string feature) {
		lock (_featureLanes) {
			if (!_featureLanes.TryGetValue(feature, out var lane)) {
				lane = new FeatureLane();
				_featureLanes.Add(feature, lane);
			}

			return lane;
		}
	}

	private void ReceiveResponse(WebPeer peer, MessageEnvelope envelope) {
		if (envelope.RequestId is not { } requestId
			|| !_outbound.TryGetValue((peer, requestId), out var request)
			|| request.Feature != envelope.Feature
			|| request.Name != envelope.Name
			|| !_outbound.TryRemove((peer, requestId), out request)) {
			return;
		}

		if (envelope.Error is { } error) {
			request.Completion.TrySetException(new InvalidOperationException(error));
		} else {
			request.Completion.TrySetResult(envelope.Payload);
		}
	}

	private void CancelOutbound(
		WebPeer peer,
		string requestId,
		string feature,
		string name,
		CancellationToken ct) {
		if (!_outbound.TryRemove((peer, requestId), out var request)) {
			return;
		}

		try {
			_sendToPeer(peer, MessageEnvelope.Cancel(Scope, Address, requestId, feature, name).ToJson());
		} catch (Exception) {
			// Cancellation already settled the request; a failed transport cannot change that outcome.
		}

		request.Completion.TrySetCanceled(ct);
	}

	private void TrySendResponse(
		WebPeer peer,
		MessageEnvelope request,
		JsonElement payload,
		string? error) {
		try {
			_sendToPeer(
				peer,
				MessageEnvelope.Response(
					Scope,
					Address,
					request.RequestId!,
					request.Feature,
					request.Name,
					error is null
						? payload
						: JsonSerializer.SerializeToElement<object?>(null),
					error).ToJson());
		} catch (Exception ex) {
			LogDiagnostic(
				$"[bridge] response delivery for {request.Feature}.{request.Name} "
				+ $"to peer '{peer.Id}' failed: {ex}");
		}
	}

	private void SendFailure(WebPeer peer, MessageEnvelope request, string error) =>
		_sendToPeer(
			peer,
			MessageEnvelope.Response(
				request.Scope,
				request.Session,
				request.RequestId!,
				request.Feature,
				request.Name,
				JsonSerializer.SerializeToElement<object?>(null),
				error).ToJson());

	private void ThrowIfClosed() => ObjectDisposedException.ThrowIf(Closed, this);

	private sealed class HandlerRegistration {
		private readonly Func<MessagePeer, JsonElement, CancellationToken, Task<HandlerResponse>> _handler;
		private readonly FeatureLane? _lane;

		public HandlerRegistration(
			Func<MessagePeer, JsonElement, CancellationToken, Task<HandlerResponse>> handler,
			SessionExecution execution,
			FeatureLane lane,
			Func<MessagePeer, bool> admit) {
			_handler = handler;
			_lane = execution == SessionExecution.Serialized ? lane : null;
			_admit = admit;
		}

		private readonly Func<MessagePeer, bool> _admit;

		public bool Admits(MessagePeer peer) => _admit(peer);

		public Task<HandlerResponse> InvokeAsync(
			MessagePeer peer,
			JsonElement payload,
			CancellationToken ct,
			Task admitted,
			IMessageHandlerExecutor handlerExecutor,
			MessageOperation operation) {
			async Task<HandlerResponse> InvokeAdmittedAsync() {
				await admitted.ConfigureAwait(false);
				operation.MarkStage("handler-dispatch");
				return await operation.SuperviseAsync(() => handlerExecutor
					.InvokeAsync(() => {
						operation.MarkStage("handler");
						return _handler(peer, payload, ct);
					}, ct)).ConfigureAwait(false);
			}

			if (_lane is null) {
				return InvokeAdmittedAsync();
			}

			return _lane.Enqueue(InvokeAdmittedAsync);
		}
	}

	private sealed class FeatureLane : IDisposable {
		private readonly object _gate = new();
		private Task _tail = Task.CompletedTask;
		private bool _disposed;

		public Task<HandlerResponse> Enqueue(Func<Task<HandlerResponse>> work) {
			lock (_gate) {
				ObjectDisposedException.ThrowIf(_disposed, this);
				var queued = RunAfterAsync(_tail, work);
				_tail = queued;
				return queued;
			}
		}

		public void Dispose() {
			lock (_gate) {
				_disposed = true;
			}
		}

		private static async Task<HandlerResponse> RunAfterAsync(
			Task predecessor,
			Func<Task<HandlerResponse>> work) {
			try {
				await predecessor.ConfigureAwait(false);
			} catch (Exception) {
				// Each dispatch reports its own failure; the lane only preserves admission order.
			}

			return await work().ConfigureAwait(false);
		}
	}

	private sealed class Registration(Action dispose) : IDisposable {
		private Action? _dispose = dispose;

		public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
	}

	private sealed record OutboundRequest(string Feature, string Name) {
		public TaskCompletionSource<JsonElement> Completion { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
	}

	private sealed record InboundRequest(
		string Feature,
		string Name,
		CancellationTokenSource Cancellation);

	private sealed record HandlerResponse(JsonElement Payload, Func<CancellationToken, Task>? AfterResponse);

	private sealed record DispatchCompletion(Func<CancellationToken, Task>? AfterResponse);

	private sealed record NoResponse {
		public static NoResponse Value { get; } = new();
	}
}
