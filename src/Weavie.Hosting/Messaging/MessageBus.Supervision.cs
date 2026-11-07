using Weavie.Core.Commands;

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
		var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
			_dispatchCancellation.Token,
			lifetime.Operation.TimeoutToken);
		bool started = false;
		bool outlived = false;
		try {
			lifetime.Operation.MarkStage(MessageStage.AfterResponse);
			await lifetime.Operation.SuperviseAsync(async () => {
				started = true;
				await afterResponse(cancellation.Token).ConfigureAwait(false);
				return true;
			}, running => {
				outlived = true;
				_ = FinishLateAsync(running, lifetime.Operation, cancellation);
			}).ConfigureAwait(false);
		} catch (MessageOperationTimeoutException) when (started) {
		} catch (MessageOperationTimeoutException) {
			// It still owes its releases: command leases, ordered completions.
			var skipped = RunAfterDeadlineAsync(afterResponse);
			outlived = true;
			_ = FinishLateAsync(skipped, lifetime.Operation, cancellation);
		} catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
		} catch (Exception ex) {
			LogDiagnostic($"[bridge] after-response work failed: {ex}");
		} finally {
			_afterResponseContext.Value = null;
			lifetime.Operation.Complete();
			SettleDispatch(lifetime);
			if (!outlived) {
				cancellation.Dispose();
			}
		}
	}

	private void SettleDispatch(DispatchLifetime lifetime) {
		lock (_lifecycle) {
			_dispatches.Remove(lifetime);
		}
		lifetime.Completion.TrySetResult();
	}

	private void OnOperationTimedOut(MessageOperation operation) {
		if (operation.Severity == OperationSeverity.SessionCritical) {
			RequireReload(operation.TimeoutDetail());
		}

		if (operation.Envelope.Kind == MessageKind.Request && operation.TimeoutOwnsResponse) {
			TrySendResponse(operation.Peer, operation.Envelope, default, operation.TimeoutDetail());
		}
	}

	private void RequireReload(string reason) {
		if (Interlocked.CompareExchange(ref _reloadReason, reason, null) is null) {
			ReloadRequiredChanged?.Invoke();
		}
	}

	// The deadline gave up on this handler, but what it owns must still release: command leases, ordered completions.
	private Task FinishLateAsync(Task<HandlerResponse> running, MessageOperation operation, CancellationTokenSource request) =>
		FinishLateAsync(RunResponseAfterDeadlineAsync(running), operation, request);

	private static async Task<bool> RunResponseAfterDeadlineAsync(Task<HandlerResponse> running) {
		var response = await running.ConfigureAwait(false);
		return response.AfterResponse is not { } afterResponse || await RunAfterDeadlineAsync(afterResponse).ConfigureAwait(false);
	}

	private static async Task<bool> RunAfterDeadlineAsync(Func<CancellationToken, Task> afterResponse) {
		await afterResponse(new CancellationToken(canceled: true)).ConfigureAwait(false);
		return true;
	}

	private async Task FinishLateAsync(Task<bool> running, MessageOperation operation, CancellationTokenSource cancellation) {
		try {
			await running.ConfigureAwait(false);
			operation.CompleteLate();
		} catch (OperationCanceledException) {
		} catch (Exception ex) {
			LogDiagnostic($"[bridge] late {operation.Envelope.Feature}.{operation.Envelope.Name} failed: {ex}");
		} finally {
			cancellation.Dispose();
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
