namespace Weavie.Hosting.Messaging;

internal partial class MessageBus {
	private void DispatchFinished(Task<DispatchCompletion> dispatch, DispatchLifetime lifetime) {
		if (dispatch.Status != TaskStatus.RanToCompletion) {
			if (dispatch.Exception is { } failure) {
				LogDiagnostic($"[bridge] message dispatch failed: {failure}");
			}

			lifetime.Operation.Complete();
			SettleDispatch(lifetime);
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

	private async Task RunAfterResponseAsync(
		Func<CancellationToken, Task> afterResponse,
		DispatchLifetime lifetime) {
		_afterResponseContext.Value = lifetime;
		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
			_dispatchCancellation.Token,
			lifetime.Operation.TimeoutToken);
		try {
			lifetime.Operation.MarkStage("after-response");
			await lifetime.Operation.SuperviseAsync(async () => {
				await afterResponse(cancellation.Token).ConfigureAwait(false);
				return true;
			}).ConfigureAwait(false);
		} catch (MessageOperationTimeoutException) {
		} catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
		} catch (Exception ex) {
			LogDiagnostic($"[bridge] after-response work failed: {ex}");
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
		Fault(detail);
		if (operation.Envelope.Kind == MessageKind.Request && operation.TimeoutOwnsResponse) {
			TrySendResponse(operation.Peer, operation.Envelope, default, detail);
		}
	}

	private void Fault(string reason) {
		if (Interlocked.Exchange(ref _isFaulted, 1) != 0) {
			return;
		}

		Volatile.Write(ref _faultReason, reason);
		Volatile.Write(ref _accepting, 0);
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

	private void LogDiagnostic(string message) => _diagnostics.Report(message);

	private sealed class DispatchLifetime(MessageOperation operation) {
		public MessageOperation Operation { get; } = operation;

		public TaskCompletionSource Completion { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
	}
}
