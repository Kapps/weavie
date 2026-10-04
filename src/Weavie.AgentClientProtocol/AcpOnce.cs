namespace Weavie.AgentClientProtocol;

/// <summary>A reference assigned exactly once; reading it before then is an error.</summary>
internal sealed class AcpOnce<T> where T : class {
	private T? _value;

	public bool IsSet => Volatile.Read(ref _value) is not null;

	public T Value => Volatile.Read(ref _value) ?? throw new InvalidOperationException($"The {typeof(T).Name} is not assigned yet.");

	public void Set(T value) {
		ArgumentNullException.ThrowIfNull(value);
		if (Interlocked.CompareExchange(ref _value, value, null) is not null) {
			throw new InvalidOperationException($"The {typeof(T).Name} is already assigned.");
		}
	}
}
