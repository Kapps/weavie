using System.Text.Json;

namespace Weavie.Hosting.Messaging;

internal sealed class MessageOperation {
	private const int Active = 0;
	private const int Completed = 1;
	private const int TimedOut = 2;

	private readonly PendingPresenter _presenter;
	private readonly Func<JsonElement, string> _activity;
	private readonly MessageExecutionPolicy _policy;
	private readonly TimeProvider _time;
	private readonly Action<MessageOperation> _slow;
	private readonly Action<MessageOperation, string> _timedOut;
	private readonly Action<MessageOperation, bool> _completed;
	private readonly CancellationTokenSource _watchdogStop = new();
	private readonly CancellationTokenSource _handlerCancellation = new();
	private readonly object _transition = new();
	private readonly object _diagnostics = new();
	private readonly TaskCompletionSource _deadline =
		new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly long _startedTimestamp;
	private string _stage = MessageStage.FeatureQueue;
	private Func<MessageOperation?> _laneRunning = static () => null;
	private int _state;
	private int _slowReported;
	private int _slowDiagnosticDelivered;
	private int _responseSettled;
	private int _timeoutOwnsResponse;

	public MessageOperation(
		string id,
		WebPeer peer,
		MessageEnvelope envelope,
		PendingPresenter presenter,
		Func<JsonElement, string> activity,
		MessageExecutionPolicy policy,
		TimeProvider time,
		Action<MessageOperation> slow,
		Action<MessageOperation, string> timedOut,
		Action<MessageOperation, bool> completed) {
		Id = id;
		Peer = peer;
		Envelope = envelope;
		_presenter = presenter;
		_activity = activity;
		_policy = policy;
		_time = time;
		_slow = slow;
		_timedOut = timedOut;
		_completed = completed;
		AcceptedAt = time.GetUtcNow();
		_startedTimestamp = time.GetTimestamp();
	}

	public string Id { get; }

	public WebPeer Peer { get; }

	public MessageEnvelope Envelope { get; }

	public DateTimeOffset AcceptedAt { get; }

	public string NotificationKey => $"message-operation:{Id}";

	/// <summary>What the user is doing, e.g. "Saving a file".</summary>
	public string Activity => _activity(Envelope.Payload);

	/// <summary>The activity this operation is queued behind in its feature lane, if another is running there.</summary>
	public string? Blocker => Volatile.Read(ref _laneRunning)() is { } running && running != this ? running.Activity : null;

	public string Stage => Volatile.Read(ref _stage);

	public CancellationToken TimeoutToken => _handlerCancellation.Token;

	public bool HasTimedOut => Volatile.Read(ref _state) == TimedOut;

	public bool TimeoutOwnsResponse => Volatile.Read(ref _timeoutOwnsResponse) != 0;

	public void StartWatchdog() {
		if (_presenter == PendingPresenter.Bus) {
			_ = WatchSlowAsync();
		}
		_ = WatchDeadlineAsync();
	}

	public void MarkStage(string stage) {
		ArgumentException.ThrowIfNullOrEmpty(stage);
		if (Volatile.Read(ref _state) == Active
			&& Interlocked.Exchange(ref _stage, stage) != stage
			&& Volatile.Read(ref _slowReported) != 0) {
			// A busy notice already shown describes the old stage; restate it.
			_slow(this);
		}
	}

	public void QueueBehind(Func<MessageOperation?> laneRunning) => Volatile.Write(ref _laneRunning, laneRunning);

	public bool TrySettleResponse() =>
		Interlocked.CompareExchange(ref _responseSettled, 1, 0) == 0;

	public async Task<T> SuperviseAsync<T>(Func<Task<T>> start) {
		ArgumentNullException.ThrowIfNull(start);
		if (HasTimedOut) {
			throw new MessageOperationTimeoutException(TimeoutDetail());
		}

		var running = start();
		var winner = await Task.WhenAny(running, _deadline.Task).ConfigureAwait(false);
		if (winner == running) {
			return await running.ConfigureAwait(false);
		}

		ObserveLate(running);
		throw new MessageOperationTimeoutException(TimeoutDetail());
	}

	public void Complete() {
		bool wasSlow;
		lock (_transition) {
			if (Volatile.Read(ref _state) != Active) {
				return;
			}

			Volatile.Write(ref _state, Completed);
			wasSlow = Volatile.Read(ref _slowReported) != 0;
		}

		_watchdogStop.Cancel();
		_completed(this, wasSlow);
	}

	public bool TryRunSlowDiagnostic(Action diagnostic) {
		ArgumentNullException.ThrowIfNull(diagnostic);
		lock (_diagnostics) {
			if (Volatile.Read(ref _state) != Active) {
				return false;
			}

			_slowDiagnosticDelivered = 1;
			diagnostic();
			return true;
		}
	}

	public void RunTimeoutDiagnostic(Action slowDiagnostic, Action terminalDiagnostic) {
		ArgumentNullException.ThrowIfNull(slowDiagnostic);
		ArgumentNullException.ThrowIfNull(terminalDiagnostic);
		lock (_diagnostics) {
			if (_slowDiagnosticDelivered == 0 && _presenter == PendingPresenter.Bus) {
				_slowDiagnosticDelivered = 1;
				slowDiagnostic();
			}

			terminalDiagnostic();
		}
	}

	public void RunTerminalDiagnostic(Action diagnostic) {
		ArgumentNullException.ThrowIfNull(diagnostic);
		lock (_diagnostics) {
			diagnostic();
		}
	}

	public MessageOperationSnapshot Snapshot() => new(
		Id,
		EndpointName(Envelope),
		Peer.Id,
		Envelope.Kind.ToString().ToLowerInvariant(),
		Envelope.RequestId,
		Envelope.Feature,
		Envelope.Name,
		Volatile.Read(ref _stage),
		AcceptedAt,
		(long)_time.GetElapsedTime(_startedTimestamp).TotalMilliseconds);

	public string TimeoutDetail() =>
		$"{Activity} didn't finish within {_policy.Deadline.TotalSeconds:0.###} seconds, so "
		+ (Envelope.Session is null ? "Weavie" : "this session") + " stopped responding.";

	private async Task WatchSlowAsync() {
		try {
			await Task.Delay(_policy.SlowAfter, _time, _watchdogStop.Token).ConfigureAwait(false);
		} catch (OperationCanceledException) when (_watchdogStop.IsCancellationRequested) {
			return;
		}

		lock (_transition) {
			if (Volatile.Read(ref _state) != Active) {
				return;
			}

			Volatile.Write(ref _slowReported, 1);
		}

		_slow(this);
	}

	private async Task WatchDeadlineAsync() {
		try {
			await Task.Delay(_policy.Deadline, _time, _watchdogStop.Token).ConfigureAwait(false);
		} catch (OperationCanceledException) when (_watchdogStop.IsCancellationRequested) {
			return;
		}

		lock (_transition) {
			if (Volatile.Read(ref _state) != Active) {
				return;
			}

			if (Interlocked.CompareExchange(ref _responseSettled, 1, 0) == 0) {
				Volatile.Write(ref _timeoutOwnsResponse, 1);
			}

			Volatile.Write(ref _state, TimedOut);
			_deadline.TrySetResult();
		}

		_ = _handlerCancellation.CancelAsync();
		_timedOut(this, TimeoutDetail());
	}

	private static void ObserveLate<T>(Task<T> running) =>
		_ = running.ContinueWith(
			static task => _ = task.Exception,
			CancellationToken.None,
			TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);

	private static string EndpointName(MessageEnvelope envelope) => envelope.Session is { } session
		? $"session:{session.Slot}/{session.Incarnation}"
		: "host";
}

internal static class MessageStage {
	public const string FeatureQueue = "feature-queue";
	public const string HandlerDispatch = "handler-dispatch";
	public const string Handler = "handler";
	public const string AfterResponse = "after-response";
}

internal sealed class MessageOperationTimeoutException(string message) : TimeoutException(message);
