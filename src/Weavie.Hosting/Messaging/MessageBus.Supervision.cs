namespace Weavie.Hosting.Messaging;

internal partial class MessageBus {
	private void DispatchFinished(Task<DispatchCompletion> dispatch, DispatchLifetime lifetime) {
		if (dispatch.Status != TaskStatus.RanToCompletion) {
			return;
		}

		var completion = dispatch.Result;
		if (completion.AfterResponse is { } afterResponse) {
			_ = Task.Run(() => RunAfterResponseAsync(afterResponse, lifetime));
		} else {
			lifetime.Operation.Complete();
			SettleDispatch(lifetime);
		}
	}

	private async Task RunAfterResponseAsync(Func<Task> afterResponse, DispatchLifetime lifetime) {
		_afterResponseContext.Value = lifetime;
		try {
			lifetime.Operation.MarkStage("after-response");
			await lifetime.Operation.SuperviseAsync(async () => {
				await afterResponse().ConfigureAwait(false);
				return true;
			}).ConfigureAwait(false);
		} catch (MessageOperationTimeoutException) {
		} catch (Exception ex) {
			_log($"[bridge] after-response work failed: {ex}");
		} finally {
			_afterResponseContext.Value = null;
			lifetime.Operation.Complete();
			SettleDispatch(lifetime);
		}
	}

	private void SettleDispatch(DispatchLifetime lifetime) {
		lock (_lifecycle) {
			_dispatches.Remove(lifetime);
		}
		lifetime.Completion.TrySetResult();
	}

	private void OnOperationTimedOut(MessageOperation operation, string detail) {
		if (operation.Envelope.Kind == MessageKind.Request && operation.TrySettleResponse()) {
			TrySendResponse(operation.Peer, operation.Envelope, default, detail);
		}

		Fault(detail);
	}

	private void Fault(string reason) {
		if (Interlocked.Exchange(ref _isFaulted, 1) != 0) {
			return;
		}

		Volatile.Write(ref _faultReason, reason);
		Volatile.Write(ref _accepting, 0);
		_log($"[message] endpoint faulted: {reason}");
		CancelDispatches();
		foreach (var request in _requests.Values) {
			_ = request.Cancellation.CancelAsync();
		}
	}

	private void CancelDispatches() {
		if (Interlocked.Exchange(ref _dispatchCancellationRequested, 1) == 0) {
			_ = _dispatchCancellation.CancelAsync();
		}
	}

	private sealed class DispatchLifetime(MessageOperation operation) {
		public MessageOperation Operation { get; } = operation;

		public TaskCompletionSource Completion { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
	}
}
