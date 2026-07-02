using System.Collections.Concurrent;

namespace Weavie.Hosting;

/// <summary>
/// Marshals an action onto the host's UI thread (WinForms <c>BeginInvoke</c>, Cocoa
/// <c>BeginInvokeOnMainThread</c>, GTK <c>GtkMain.Invoke</c>; headless runs a dedicated serial thread). Always
/// fire-and-forget: a synchronous hop would deadlock the PTY-teardown path the bridges document. Session state
/// (the active session, the slot set) is only touched from this thread, so posted work never races a switch.
/// </summary>
public interface IUiDispatcher {
	/// <summary>Queues <paramref name="action"/> to run on the UI thread (or inline when there is none).</summary>
	void Post(Action action);
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
/// gets from its UI thread (a session switch and a session-scoped push can never interleave).
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
