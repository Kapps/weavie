namespace Weavie.Hosting;

/// <summary>The one continuation a session's page owes an answer to; setting a new one drops the previous.</summary>
/// <typeparam name="T">The continuation.</typeparam>
internal sealed class PendingReply<T> where T : class {
	private T? _value;

	public void Set(T value) => Volatile.Write(ref _value, value);

	/// <summary>Removes and returns the continuation, or null when none is pending.</summary>
	public T? Take() => Interlocked.Exchange(ref _value, null);
}
