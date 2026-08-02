namespace Weavie.Hosting.Messaging;

internal sealed class MessageOperation {
	private const int Active = 0;
	private const int Completed = 1;
	private const int TimedOut = 2;

	private readonly MessageExecutionPolicy _policy;
	private readonly TimeProvider _time;
	private readonly Action<MessageOperation> _slow;
	private readonly Action<MessageOperation, string> _timedOut;
	private readonly Action<MessageOperation, bool> _completed;
	private readonly CancellationTokenSource _watchdogStop = new();
	private readonly CancellationTokenSource _handlerCancellation = new();
	private readonly object _transition = new();
	private readonly TaskCompletionSource _deadline =
		new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly long _startedTimestamp;
	private string _stage = "feature-queue";
	private int _state;
	private int _slowReported;
	private int _responseSettled;

	public MessageOperation(
		string id,
		WebPeer peer,
		MessageEnvelope envelope,
		MessageExecutionPolicy policy,
		TimeProvider time,
		Action<MessageOperation> slow,
		Action<MessageOperation, string> timedOut,
		Action<MessageOperation, bool> completed) {
		Id = id;
		Peer = peer;
		Envelope = envelope;
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

	public CancellationToken TimeoutToken => _handlerCancellation.Token;

	public bool HasTimedOut => Volatile.Read(ref _state) == TimedOut;

	public void StartWatchdog() => _ = WatchAsync();

	public void MarkStage(string stage) {
		ArgumentException.ThrowIfNullOrEmpty(stage);
		if (Volatile.Read(ref _state) == Active) {
			Volatile.Write(ref _stage, stage);
		}
	}

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
		lock (_transition) {
			if (Volatile.Read(ref _state) != Active) {
				return;
			}

			Volatile.Write(ref _state, Completed);
			_watchdogStop.Cancel();
			_completed(this, Volatile.Read(ref _slowReported) != 0);
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

	public string TimeoutDetail() {
		var snapshot = Snapshot();
		return $"Message operation {snapshot.Id} timed out after {snapshot.ElapsedMs} ms: "
			+ $"{snapshot.Endpoint} {snapshot.Feature}.{snapshot.Name}, stage {snapshot.Stage}, "
			+ $"peer {snapshot.Peer}, request {snapshot.RequestId ?? "-"}.";
	}

	private async Task WatchAsync() {
		try {
			await Task.Delay(_policy.SlowAfter, _time, _watchdogStop.Token).ConfigureAwait(false);
			lock (_transition) {
				if (Volatile.Read(ref _state) != Active) {
					return;
				}

				Volatile.Write(ref _slowReported, 1);
				_slow(this);
			}
			await Task.Delay(_policy.Deadline - _policy.SlowAfter, _time, _watchdogStop.Token).ConfigureAwait(false);
		} catch (OperationCanceledException) when (_watchdogStop.IsCancellationRequested) {
			return;
		}

		lock (_transition) {
			if (Volatile.Read(ref _state) != Active) {
				return;
			}

			Volatile.Write(ref _state, TimedOut);
			string detail = TimeoutDetail();
			_deadline.TrySetResult();
			_ = _handlerCancellation.CancelAsync();
			_timedOut(this, detail);
		}
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

internal sealed class MessageOperationTimeoutException(string message) : TimeoutException(message);
