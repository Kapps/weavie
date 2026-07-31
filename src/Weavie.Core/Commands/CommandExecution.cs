namespace Weavie.Core.Commands;

/// <summary>
/// One command invocation's reply boundary. Handlers may defer endpoint-destroying work until the caller has
/// delivered <see cref="Result"/>; ordinary in-process invocation completes that work before returning.
/// </summary>
public sealed class CommandExecution {
	private Func<Task>? _afterReply;

	internal CommandExecution(CommandResult result, Func<Task>? afterReply) {
		Result = result;
		_afterReply = afterReply;
	}

	/// <summary>The command outcome to deliver before completing deferred work.</summary>
	public CommandResult Result { get; }

	/// <summary>Creates an execution with no deferred work.</summary>
	public static CommandExecution Completed(CommandResult result) => new(result, null);

	/// <summary>Runs deferred work once, after the command outcome has been delivered.</summary>
	public Task CompleteAsync() =>
		Interlocked.Exchange(ref _afterReply, null)?.Invoke()
		?? Task.CompletedTask;
}

/// <summary>Lets a command handler schedule work that would destroy the transport carrying its own result.</summary>
public sealed class CommandInvocationContext {
	private readonly List<Func<Task>> _afterReply = [];
	private bool _sealed;

	/// <summary>Schedules <paramref name="action"/> after the caller delivers the command result.</summary>
	public void AfterReply(Func<Task> action) {
		ArgumentNullException.ThrowIfNull(action);
		ObjectDisposedException.ThrowIf(_sealed, this);
		_afterReply.Add(action);
	}

	internal Func<Task>? Seal() {
		_sealed = true;
		if (_afterReply.Count == 0) {
			return null;
		}

		Func<Task>[] actions = [.. _afterReply];
		return async () => {
			foreach (var action in actions) {
				await action().ConfigureAwait(false);
			}
		};
	}
}
