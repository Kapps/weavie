using System.Collections.Concurrent;

namespace Weavie.Hosting;

/// <summary>
/// Marshals work onto the host's UI thread (WinForms <c>BeginInvoke</c>, Cocoa
/// <c>BeginInvokeOnMainThread</c>, GTK <c>GtkMain.Invoke</c>; headless runs a dedicated serial thread). Dispatch is
/// always asynchronous because a synchronous hop would deadlock the PTY-teardown path the bridges document.
/// Host catalog mutations are serialized here; session message routing does not depend on presentation selection.
/// </summary>
public interface IUiDispatcher {
	/// <summary>Queues <paramref name="action"/> to run on the UI thread (or inline when there is none).</summary>
	void Post(Action action);
}

/// <summary>Awaitable, non-blocking entry to an <see cref="IUiDispatcher"/>.</summary>
public static class UiDispatcherExtensions {
	private const int EntryPending = 0;
	private const int EntryStarted = 1;
	private const int EntryCanceled = 2;

	/// <summary>
	/// Starts <paramref name="action"/> on the UI thread and propagates its completion and logical execution
	/// context across dispatchers whose native queues do not flow it themselves.
	/// </summary>
	public static Task InvokeAsync(
		this IUiDispatcher dispatcher,
		Func<Task> action,
		CancellationToken ct) =>
		InvokeCoreAsync(
			dispatcher,
			async () => {
				await action().ConfigureAwait(false);
				return true;
			},
			ct);

	/// <summary>Starts <paramref name="action"/> on the UI thread and propagates its result.</summary>
	public static Task<T> InvokeAsync<T>(
		this IUiDispatcher dispatcher,
		Func<Task<T>> action,
		CancellationToken ct) =>
		InvokeCoreAsync(dispatcher, action, ct);

	private static async Task<T> InvokeCoreAsync<T>(
		IUiDispatcher dispatcher,
		Func<Task<T>> action,
		CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(dispatcher);
		ArgumentNullException.ThrowIfNull(action);
		ct.ThrowIfCancellationRequested();
		var executionContext = ExecutionContext.Capture();
		var started = new TaskCompletionSource<Task<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
		int entryState = EntryPending;
		// Cancellation can prevent UI entry; once entry starts, drain the action to preserve dispatcher ordering.
		using var cancellation = ct.Register(() => {
			if (Interlocked.CompareExchange(ref entryState, EntryCanceled, EntryPending) == EntryPending) {
				started.TrySetCanceled(ct);
			}
		});
		dispatcher.Post(() => {
			if (Interlocked.CompareExchange(ref entryState, EntryStarted, EntryPending) != EntryPending) {
				return;
			}

			void Start() {
				try {
					started.TrySetResult(action());
				} catch (Exception ex) {
					started.TrySetException(ex);
				}
			}

			if (executionContext is null) {
				Start();
			} else {
				ExecutionContext.Run(executionContext, _ => Start(), null);
			}
		});

		var running = await started.Task.ConfigureAwait(false);
		return await running.ConfigureAwait(false);
	}
}

/// <summary>An <see cref="IUiDispatcher"/> that runs actions inline — for tests that drive the host single-threaded.</summary>
public sealed class InlineUiDispatcher : IUiDispatcher {
	/// <inheritdoc/>
	public void Post(Action action) {
		ArgumentNullException.ThrowIfNull(action);
		action();
	}
}

/// <summary>
/// An <see cref="IUiDispatcher"/> backed by one dedicated consumer thread — the "UI thread" of a host with no
/// native toolkit. Actions run strictly in Post order, giving a headless host the serialization a native host
/// gets from its UI thread for host-owned state changes.
/// </summary>
public sealed class SerialUiDispatcher : IUiDispatcher {
	private readonly BlockingCollection<Action> _queue = [];
	private readonly Action<Exception> _onError;

	/// <summary>Starts the consumer thread; an action that throws is reported to <paramref name="onError"/> and the pump continues.</summary>
	public SerialUiDispatcher(Action<Exception> onError) {
		ArgumentNullException.ThrowIfNull(onError);
		_onError = onError;
		new Thread(Pump) { IsBackground = true, Name = "weavie-ui" }.Start();
	}

	/// <inheritdoc/>
	public void Post(Action action) {
		ArgumentNullException.ThrowIfNull(action);
		_queue.Add(action);
	}

	private void Pump() {
		foreach (var action in _queue.GetConsumingEnumerable()) {
			try {
				action();
			} catch (Exception ex) {
				_onError(ex);
			}
		}
	}
}

/// <summary>An <see cref="IUiDispatcher"/> backed by a marshal delegate the host supplies (its native BeginInvoke).</summary>
public sealed class DelegateUiDispatcher : IUiDispatcher {
	private readonly Action<Action> _post;

	/// <summary>Creates a dispatcher that forwards each action to <paramref name="post"/> (the host's UI-thread marshal).</summary>
	public DelegateUiDispatcher(Action<Action> post) {
		ArgumentNullException.ThrowIfNull(post);
		_post = post;
	}

	/// <inheritdoc/>
	public void Post(Action action) {
		ArgumentNullException.ThrowIfNull(action);
		_post(action);
	}
}
