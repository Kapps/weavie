using System.Runtime.ExceptionServices;

namespace Weavie.Win.Hosting;

internal static class ShutdownFailure {
	public static Exception Add(Exception? current, Exception next) =>
		current is null ? next : new AggregateException(current, next);

	public static void ThrowIfAny(Exception? failure) {
		if (failure is not null) {
			ExceptionDispatchInfo.Capture(failure).Throw();
		}
	}
}
