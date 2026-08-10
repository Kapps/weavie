using System.Threading.Channels;
using Weavie.Core.Agents;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting.Agents;

internal sealed class AgentPaneOutput : IAsyncDisposable {
	private readonly Channel<OutputCommand> _commands = Channel.CreateUnbounded<OutputCommand>(
		new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
	private readonly IMessageFeatureTarget _broadcast;
	private readonly TimeSpan _window;
	private readonly Action<string> _log;
	private readonly List<AgentPaneRecord> _buffer = [];
	private readonly Task _worker;
	private long _flushVersion;
	private int _closed;

	public AgentPaneOutput(IMessageFeatureTarget broadcast, long windowMs, Action<string> log) {
		ArgumentNullException.ThrowIfNull(broadcast);
		ArgumentOutOfRangeException.ThrowIfNegative(windowMs);
		ArgumentNullException.ThrowIfNull(log);
		_broadcast = broadcast;
		_window = TimeSpan.FromMilliseconds(windowMs);
		_log = log;
		_worker = Task.Run(RunAsync);
	}

	public void Live(AgentPaneRecord message) {
		ArgumentNullException.ThrowIfNull(message);
		Write(new LiveCommand(message));
	}

	public void Reset() => Write(ResetCommand.Instance);

	public async Task DrainAsync(CancellationToken ct) {
		var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		Write(new BarrierCommand(completion));
		var settled = await Task.WhenAny(completion.Task, _worker).WaitAsync(ct).ConfigureAwait(false);
		await settled.ConfigureAwait(false);
	}

	public async ValueTask DisposeAsync() {
		if (Interlocked.Exchange(ref _closed, 1) == 0) {
			_commands.Writer.TryComplete();
		}

		await _worker.ConfigureAwait(false);
	}

	private void Write(OutputCommand command) {
		if (Volatile.Read(ref _closed) == 0 && _commands.Writer.TryWrite(command)) {
			return;
		}

		throw new ObjectDisposedException(nameof(AgentPaneOutput));
	}

	private async Task RunAsync() {
		Exception? pendingFailure = null;
		await foreach (var command in _commands.Reader.ReadAllAsync().ConfigureAwait(false)) {
			try {
				switch (command) {
					case LiveCommand live:
						_buffer.Add(live.Message);
						if (_window == TimeSpan.Zero) {
							Flush();
						} else if (_buffer.Count == 1) {
							ScheduleFlush(++_flushVersion);
						}

						break;
					case ResetCommand:
						Discard();
						_broadcast.Publish("paneReset", new { });
						break;
					case FlushCommand flush when flush.Version == _flushVersion:
						Flush();
						break;
					case BarrierCommand barrier:
						Flush();
						if (pendingFailure is { } failure) {
							pendingFailure = null;
							barrier.Completion.TrySetException(failure);
						} else {
							barrier.Completion.TrySetResult();
						}
						break;
				}
			} catch (Exception ex) {
				_log($"[agent-pane] output failed: {ex}");
				if (command is BarrierCommand barrier) {
					pendingFailure = null;
					barrier.Completion.TrySetException(ex);
				} else {
					pendingFailure ??= ex;
				}
			}
		}

		try {
			Flush();
		} catch (Exception ex) {
			_log($"[agent-pane] final output flush failed: {ex}");
		}
	}

	private void ScheduleFlush(long version) => _ = Task.Run(async () => {
		await Task.Delay(_window).ConfigureAwait(false);
		_commands.Writer.TryWrite(new FlushCommand(version));
	});

	private void Flush() {
		if (_buffer.Count == 0) {
			return;
		}

		var batch = _buffer.ToArray();
		_buffer.Clear();
		_flushVersion++;
		if (batch.Length == 1) {
			_broadcast.Publish("pane", AgentPaneProtocol.Message(batch[0]));
		} else {
			_broadcast.Publish("paneBatch", AgentPaneProtocol.Batch(batch));
		}
	}

	private void Discard() {
		_buffer.Clear();
		_flushVersion++;
	}

	private abstract record OutputCommand;

	private sealed record LiveCommand(AgentPaneRecord Message) : OutputCommand;

	private sealed record FlushCommand(long Version) : OutputCommand;

	private sealed record BarrierCommand(TaskCompletionSource Completion) : OutputCommand;

	private sealed record ResetCommand : OutputCommand {
		public static ResetCommand Instance { get; } = new();
	}
}
