using System.Threading.Channels;
using Weavie.Core.FileSystem;

namespace Weavie.Core.FileActivity;

/// <summary>
/// Owns one session's ordered file-activity stream and workspace invalidation source. Facts are transient and
/// exact-owner scoped; reconnect recovery remains the session lifecycle snapshot rather than activity replay.
/// </summary>
public sealed class SessionFileActivity : IFileActivitySink, IAsyncDisposable {
	private readonly Lock _gate = new();
	private readonly Channel<ActivityCommand> _commands = Channel.CreateUnbounded<ActivityCommand>(
		new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
	private readonly List<Subscription> _subscriptions = [];
	private readonly List<Exception> _faults = [];
	private readonly WorkspaceInvalidationWatcher _watcher;
	private readonly Task _worker;
	private long _nextSequence;
	private bool _accepting = true;
	private bool _closing;
	private bool _observing;
	private Task? _disposeTask;

	/// <summary>Creates a dormant activity stream; call <see cref="StartObserving"/> after consumers are wired.</summary>
	public SessionFileActivity(
		string workspaceRoot,
		Action<string> log,
		int watcherDebounceMs) {
		ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
		ArgumentNullException.ThrowIfNull(log);
		_watcher = new WorkspaceInvalidationWatcher(
			workspaceRoot,
			ReportInvalidated,
			log,
			watcherDebounceMs);
		_worker = Task.Run(ProcessAsync);
	}

	/// <summary>Registers an ordered consumer and its required, user-visible failure handler.</summary>
	public IDisposable Subscribe(
		string name,
		Func<FileActivityFact, Task> consumeAsync,
		Func<FileActivityFailure, Task> onFailureAsync) {
		ArgumentException.ThrowIfNullOrEmpty(name);
		ArgumentNullException.ThrowIfNull(consumeAsync);
		ArgumentNullException.ThrowIfNull(onFailureAsync);
		var subscription = new Subscription(this, name, consumeAsync, onFailureAsync);
		lock (_gate) {
			ObjectDisposedException.ThrowIf(_closing, this);
			_subscriptions.Add(subscription);
		}
		return subscription;
	}

	/// <summary>Starts the owned workspace watcher after every session consumer has been registered.</summary>
	public void StartObserving() {
		lock (_gate) {
			ObjectDisposedException.ThrowIf(_closing, this);
			if (_observing) {
				return;
			}
			_observing = true;
		}
		_watcher.Start();
	}

	/// <summary>Stops watcher admission, flushes its pending debounce batch, and waits for callbacks to return.</summary>
	public Task StopObservingAsync() => _watcher.StopAsync();

	/// <inheritdoc/>
	public FileActivityTicket ReportBufferSaved(string path, FileStat revision) {
		string normalized = Normalize(path);
		return Admit(sequence => new BufferSaved(sequence, normalized, revision), internalAdmission: false);
	}

	/// <inheritdoc/>
	public FileActivityTicket ReportChanged(string path, FileStat revision) {
		string normalized = Normalize(path);
		return Admit(sequence => new FileChanged(sequence, normalized, revision), internalAdmission: false);
	}

	/// <inheritdoc/>
	public FileActivityTicket ReportDeleted(string path) {
		string normalized = Normalize(path);
		return Admit(sequence => new FileDeleted(sequence, normalized), internalAdmission: false);
	}

	/// <summary>Waits until all facts admitted before this call and their snapshotted consumers settle.</summary>
	public Task DrainAsync(CancellationToken ct) {
		Task completion;
		lock (_gate) {
			if (!_accepting) {
				completion = _worker;
			} else {
				var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
				Write(new BarrierCommand(barrier));
				completion = barrier.Task;
			}
		}
		return completion.WaitAsync(ct);
	}

	/// <inheritdoc/>
	public ValueTask DisposeAsync() {
		lock (_gate) {
			if (_disposeTask is null) {
				_closing = true;
				_disposeTask = DisposeCoreAsync();
			}
			return new ValueTask(_disposeTask);
		}
	}

	private FileActivityTicket Admit(Func<long, FileActivityFact> create, bool internalAdmission) {
		lock (_gate) {
			ObjectDisposedException.ThrowIf(!_accepting || (_closing && !internalAdmission), this);
			long sequence = ++_nextSequence;
			var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			Write(new FactCommand(create(sequence), [.. _subscriptions], settled));
			return new FileActivityTicket(sequence, settled.Task);
		}
	}

	private void ReportInvalidated(IReadOnlyList<FileInvalidation> changes) {
		if (changes.Count > 0) {
			Admit(sequence => new FilesInvalidated(sequence, changes), internalAdmission: true);
		}
	}

	private async Task ProcessAsync() {
		await foreach (var command in _commands.Reader.ReadAllAsync().ConfigureAwait(false)) {
			switch (command) {
				case FactCommand fact:
					await ProcessFactAsync(fact).ConfigureAwait(false);
					break;
				case BarrierCommand barrier:
					Complete(barrier.Completion);
					break;
			}
		}
		ThrowIfFaulted();
	}

	private async Task ProcessFactAsync(FactCommand command) {
		Exception? failureHandlerError = null;
		foreach (var subscription in command.Subscriptions) {
			try {
				await subscription.ConsumeAsync(command.Fact).ConfigureAwait(false);
			} catch (Exception ex) {
				try {
					await subscription.OnFailureAsync(
						new FileActivityFailure(subscription.Name, command.Fact, ex)).ConfigureAwait(false);
				} catch (Exception handlerError) {
					failureHandlerError = new AggregateException(ex, handlerError);
					_faults.Add(failureHandlerError);
				}
			}
		}

		if (failureHandlerError is null) {
			command.Settled.TrySetResult();
		} else {
			command.Settled.TrySetException(failureHandlerError);
		}
	}

	private async Task DisposeCoreAsync() {
		await Task.Yield();
		await StopObservingAsync().ConfigureAwait(false);
		lock (_gate) {
			_accepting = false;
			_commands.Writer.TryComplete();
		}
		await _worker.ConfigureAwait(false);
	}

	private void Remove(Subscription subscription) {
		lock (_gate) {
			_subscriptions.Remove(subscription);
		}
	}

	private void Complete(TaskCompletionSource completion) {
		if (_faults.Count == 0) {
			completion.TrySetResult();
		} else {
			completion.TrySetException(new AggregateException(_faults));
		}
	}

	private void ThrowIfFaulted() {
		if (_faults.Count > 0) {
			throw new AggregateException(_faults);
		}
	}

	private void Write(ActivityCommand command) {
		if (!_commands.Writer.TryWrite(command)) {
			throw new ObjectDisposedException(nameof(SessionFileActivity));
		}
	}

	private static string Normalize(string path) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		return Path.GetFullPath(path);
	}

	private abstract record ActivityCommand;

	private sealed record FactCommand(
		FileActivityFact Fact,
		IReadOnlyList<Subscription> Subscriptions,
		TaskCompletionSource Settled) : ActivityCommand;

	private sealed record BarrierCommand(TaskCompletionSource Completion) : ActivityCommand;

	private sealed class Subscription : IDisposable {
		private SessionFileActivity? _owner;

		public Subscription(
			SessionFileActivity owner,
			string name,
			Func<FileActivityFact, Task> consumeAsync,
			Func<FileActivityFailure, Task> onFailureAsync) {
			_owner = owner;
			Name = name;
			ConsumeAsync = consumeAsync;
			OnFailureAsync = onFailureAsync;
		}

		public string Name { get; }

		public Func<FileActivityFact, Task> ConsumeAsync { get; }

		public Func<FileActivityFailure, Task> OnFailureAsync { get; }

		public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Remove(this);
	}
}
