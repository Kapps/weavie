namespace Weavie.Hosting;

/// <summary>Routes failed posted actions to their owner's error surface.</summary>
public sealed class GuardedUiDispatcher(IUiDispatcher inner, Action<Exception> report) : IUiDispatcher {
	/// <inheritdoc/>
	public void Post(Action action) => inner.Post(() => Run(action, report));

	/// <summary>Runs synchronous UI work, reporting failure without hiding a failure of the reporter itself.</summary>
	public static void Run(Action action, Action<Exception> report) {
		try {
			action();
		} catch (Exception failure) {
			try {
				report(failure);
			} catch (Exception reportingFailure) {
				throw new AggregateException("UI action and its error reporter failed", failure, reportingFailure);
			}
		}
	}
}
