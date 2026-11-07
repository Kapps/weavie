using Weavie.Core.Commands;

namespace Weavie.Hosting.Messaging;

internal sealed class MessageOperation {
	private const int Active = 0;
	private const int Completed = 1;
	private const int TimedOut = 2;

	private readonly PendingPresenter _presenter;
	private readonly MessageActivity _activity;
	private readonly MessageExecutionPolicy _policy;
	private readonly TimeProvider _time;
	private readonly Action<MessageOperation> _slow;
	private readonly Action<MessageOperation> _stillRunning;
	private readonly Action<MessageOperation> _timedOut;
	private readonly Action<MessageOperation, bool> _completed;
	private readonly Action<MessageOperation> _lateSuccess;
	private readonly CancellationTokenSource _watchdogStop = new();
	private readonly CancellationTokenSource _handlerCancellation = new();
	private readonly object _transition = new();
	private readonly object _diagnostics = new();
	private readonly TaskCompletionSource _deadline =
		new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly long _startedTimestamp;
	private string _stage = MessageStage.FeatureQueue;
	private string? _timeoutDetail;
	private Func<MessageOperation?> _laneRunning = static () => null;
	private int _state;
	private int _entered;
	private int _slowReported;
	private int _stillRunningReported;
	private int _responseSettled;
	private int _timeoutOwnsResponse;

	public MessageOperation(
		string id,
		WebPeer peer,
		MessageEnvelope envelope,
		PendingPresenter presenter,
		MessageActivity activity,
		MessageExecutionPolicy policy,
		TimeProvider time,
		Action<MessageOperation> slow,
		Action<MessageOperation> stillRunning,
		Action<MessageOperation> timedOut,
		Action<MessageOperation, bool> completed,
		Action<MessageOperation> lateSuccess) {
		Id = id;
		Peer = peer;
		Envelope = envelope;
		_presenter = presenter;
		_activity = activity;
		_policy = policy;
		_time = time;
		_slow = slow;
		_stillRunning = stillRunning;
		_timedOut = timedOut;
		_completed = completed;
		_lateSuccess = lateSuccess;
		AcceptedAt = time.GetUtcNow();
		_startedTimestamp = time.GetTimestamp();
	}

	public string Id { get; }

	public WebPeer Peer { get; }

	public MessageEnvelope Envelope { get; }

	public DateTimeOffset AcceptedAt { get; }

	public string NotificationKey => $"message-operation:{Id}";

	/// <summary>What the user is doing, e.g. "Saving a file".</summary>
	public string Activity => _activity.Label;

	/// <summary>What reaching the deadline means for this operation.</summary>
	public OperationSeverity Severity => _activity.Severity;

	/// <summary>True when the handler never started, so the deadline expired while this waited its turn.</summary>
	public bool NeverRan => Volatile.Read(ref _entered) == 0;

	/// <summary>Completes when the deadline ends this operation.</summary>
	public Task Expired => _deadline.Task;

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
			&& Volatile.Read(ref _slowReported) != 0
			&& Volatile.Read(ref _stillRunningReported) == 0) {
			// A busy notice already shown describes the old stage; restate it.
			_slow(this);
		}
	}

	public void QueueBehind(Func<MessageOperation?> laneRunning) => Volatile.Write(ref _laneRunning, laneRunning);

	/// <summary>Claims the handler's start; false once the deadline has already ended this operation.</summary>
	public bool TryEnter() {
		lock (_transition) {
			if (Volatile.Read(ref _state) != Active) {
				return false;
			}

			Volatile.Write(ref _entered, 1);
			return true;
		}
	}

	/// <summary>Stops a running no-deadline operation at the user's request.</summary>
	public void Cancel() => _ = _handlerCancellation.CancelAsync();

	/// <summary>Reports that a handler the deadline gave up on finished successfully after all.</summary>
	public void CompleteLate() => _lateSuccess(this);

	public bool TrySettleResponse() =>
		Interlocked.CompareExchange(ref _responseSettled, 1, 0) == 0;

	public async Task<T> SuperviseAsync<T>(Func<Task<T>> start, Action<Task<T>> late) {
		ArgumentNullException.ThrowIfNull(start);
		ArgumentNullException.ThrowIfNull(late);
		if (HasTimedOut) {
			throw new MessageOperationTimeoutException(TimeoutDetail());
		}

		var running = start();
		var winner = await Task.WhenAny(running, _deadline.Task).ConfigureAwait(false);
		if (winner == running) {
			return await running.ConfigureAwait(false);
		}

		late(running);
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

			diagnostic();
			return true;
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
		Severity != OperationSeverity.NoDeadline,
		AcceptedAt,
		(long)_time.GetElapsedTime(_startedTimestamp).TotalMilliseconds);

	/// <summary>Why the deadline ended this operation, as it stood at that moment; a blocker can finish before delivery.</summary>
	public string TimeoutDetail() => Volatile.Read(ref _timeoutDetail) ?? DescribeTimeout();

	private string DescribeTimeout() => NeverRan
		? $"{Activity} didn't start within {DeadlineSeconds} seconds"
			+ (Blocker is { } blocker ? $" because {blocker} is stuck." : ".")
		: $"{Activity} didn't finish within {DeadlineSeconds} seconds and may still complete.";

	public string StillRunningDetail() => $"{Activity} is still running after {DeadlineSeconds} seconds.";

	private string DeadlineSeconds => _policy.Deadline.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

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

		if (Severity == OperationSeverity.NoDeadline) {
			Volatile.Write(ref _stillRunningReported, 1);
			_stillRunning(this);
			return;
		}

		lock (_transition) {
			if (Volatile.Read(ref _state) != Active) {
				return;
			}

			if (Interlocked.CompareExchange(ref _responseSettled, 1, 0) == 0) {
				Volatile.Write(ref _timeoutOwnsResponse, 1);
			}

			Volatile.Write(ref _timeoutDetail, DescribeTimeout());
			Volatile.Write(ref _state, TimedOut);
			_deadline.TrySetResult();
		}

		_ = _handlerCancellation.CancelAsync();
		_timedOut(this);
	}

	/// <summary>Observes work the deadline gave up on, which has nothing left to report.</summary>
	public static void ObserveLate<T>(Task<T> running) =>
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
