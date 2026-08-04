using System.Threading.Channels;
using Weavie.Core.Agents;
using Weavie.Core.FileSystem;
using Weavie.Core.Sessions;

namespace Weavie.Hosting.Agents;

internal sealed class AgentPaneJournal : IAsyncDisposable {
	private readonly Channel<JournalCommand> _commands = Channel.CreateUnbounded<JournalCommand>(
		new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
	private readonly IFileSystem _fileSystem;
	private readonly string _path;
	private readonly Action<IReadOnlyList<AgentPaneMessage>> _loaded;
	private readonly Action<string> _log;
	private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly Task _worker;
	private int _closed;

	public AgentPaneJournal(
		IFileSystem fileSystem,
		string path,
		Action<IReadOnlyList<AgentPaneMessage>> loaded,
		Action<string> log) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentException.ThrowIfNullOrEmpty(path);
		ArgumentNullException.ThrowIfNull(loaded);
		ArgumentNullException.ThrowIfNull(log);
		_fileSystem = fileSystem;
		_path = path;
		_loaded = loaded;
		_log = log;
		_worker = Task.Run(RunAsync);
	}

	public void Append(AgentPaneMessage message) {
		ArgumentNullException.ThrowIfNull(message);
		Write(new AppendCommand(message));
	}

	public void Clear() => Write(ClearCommand.Instance);

	public Task WaitUntilReadyAsync(CancellationToken ct) => _ready.Task.WaitAsync(ct);

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

	private void Write(JournalCommand command) {
		if (Volatile.Read(ref _closed) == 0 && _commands.Writer.TryWrite(command)) {
			return;
		}

		throw new ObjectDisposedException(nameof(AgentPaneJournal));
	}

	private async Task RunAsync() {
		try {
			_log($"[agent-pane] loading transcript {_path}");
			var store = new AgentPaneTranscriptStore(_fileSystem, _path, _log);
			Exception? pendingFailure = null;
			var snapshot = store.Snapshot();
			try {
				_loaded(snapshot);
			} catch (Exception ex) {
				pendingFailure = ex;
				_log($"[agent-pane] transcript load callback failed: {ex}");
			}
			if (pendingFailure is { } loadFailure) {
				_ready.TrySetException(loadFailure);
			} else {
				_ready.TrySetResult();
			}
			_log($"[agent-pane] loaded {snapshot.Count} transcript messages from {_path}");

			await foreach (var command in _commands.Reader.ReadAllAsync().ConfigureAwait(false)) {
				try {
					switch (command) {
						case AppendCommand append:
							store.Append(append.Message);
							break;
						case ClearCommand:
							store.Clear();
							break;
						case BarrierCommand barrier:
							if (pendingFailure is { } failure) {
								pendingFailure = null;
								barrier.Completion.TrySetException(failure);
							} else {
								barrier.Completion.TrySetResult();
							}
							break;
					}
				} catch (Exception ex) {
					_log($"[agent-pane] transcript operation failed: {ex}");
					if (command is BarrierCommand barrier) {
						pendingFailure = null;
						barrier.Completion.TrySetException(ex);
					} else {
						pendingFailure ??= ex;
					}
				}
			}
		} catch (Exception ex) {
			_ready.TrySetException(ex);
			_log($"[agent-pane] transcript worker failed for {_path}: {ex}");
			throw;
		}
	}

	private abstract record JournalCommand;

	private sealed record AppendCommand(AgentPaneMessage Message) : JournalCommand;

	private sealed record BarrierCommand(TaskCompletionSource Completion) : JournalCommand;

	private sealed record ClearCommand : JournalCommand {
		public static ClearCommand Instance { get; } = new();
	}
}
