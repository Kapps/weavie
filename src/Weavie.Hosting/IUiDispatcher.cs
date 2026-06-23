namespace Weavie.Hosting;

/// <summary>
/// Marshals an action onto the host's UI thread (WinForms <c>BeginInvoke</c>, Cocoa
/// <c>BeginInvokeOnMainThread</c>, GTK <c>GtkMain.Invoke</c>; headless dispatches inline). Always fire-and-forget:
/// a synchronous hop would deadlock the PTY-teardown path the bridges document.
/// </summary>
public interface IUiDispatcher {
	/// <summary>Queues <paramref name="action"/> to run on the UI thread (or inline when there is none).</summary>
	void Post(Action action);
}

/// <summary>An <see cref="IUiDispatcher"/> that runs actions inline (no UI thread) — the headless default.</summary>
public sealed class InlineUiDispatcher : IUiDispatcher {
	/// <inheritdoc/>
	public void Post(Action action) {
		ArgumentNullException.ThrowIfNull(action);
		action();
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
