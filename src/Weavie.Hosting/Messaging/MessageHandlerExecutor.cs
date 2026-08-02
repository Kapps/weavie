namespace Weavie.Hosting.Messaging;

internal interface IMessageHandlerExecutor {
	Task<T> InvokeAsync<T>(Func<Task<T>> handler, CancellationToken ct);
}

internal sealed class ThreadPoolMessageHandlerExecutor : IMessageHandlerExecutor {
	public static ThreadPoolMessageHandlerExecutor Instance { get; } = new();

	private ThreadPoolMessageHandlerExecutor() {
	}

	public Task<T> InvokeAsync<T>(Func<Task<T>> handler, CancellationToken ct) {
		ct.ThrowIfCancellationRequested();
		return Task.Run(handler, ct);
	}
}

internal sealed class UiMessageHandlerExecutor : IMessageHandlerExecutor {
	private readonly IUiDispatcher _dispatcher;

	public UiMessageHandlerExecutor(IUiDispatcher dispatcher) {
		ArgumentNullException.ThrowIfNull(dispatcher);
		_dispatcher = dispatcher;
	}

	public Task<T> InvokeAsync<T>(Func<Task<T>> handler, CancellationToken ct) =>
		_dispatcher.InvokeAsync(handler, ct);
}
