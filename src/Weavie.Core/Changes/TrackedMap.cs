using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace Weavie.Core.Changes;

internal sealed class TrackedMap<T>(IEqualityComparer<string> comparer, Action<string> changed) : IReadOnlyDictionary<string, T> {
	private readonly Dictionary<string, T> _values = new(comparer);
	public T this[string key] {
		get => _values[key];
		set {
			if (_values.TryGetValue(key, out var previous) && EqualityComparer<T>.Default.Equals(previous, value)) return;
			_values[key] = value;
			changed(key);
		}
	}
	public IEnumerable<string> Keys => _values.Keys;
	public IEnumerable<T> Values => _values.Values;
	public int Count => _values.Count;
	public bool ContainsKey(string key) => _values.ContainsKey(key);
	public bool TryGetValue(string key, [MaybeNullWhen(false)] out T value) => _values.TryGetValue(key, out value);
	public bool TryAdd(string key, T value) {
		if (!_values.TryAdd(key, value)) return false;
		changed(key);
		return true;
	}
	public void Add(string key, T value) { _values.Add(key, value); changed(key); }
	public bool Remove(string key) {
		if (!_values.Remove(key)) return false;
		changed(key);
		return true;
	}
	public IEnumerator<KeyValuePair<string, T>> GetEnumerator() => _values.GetEnumerator();
	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class TrackedSet(IEqualityComparer<string> comparer, Action<string> changed) {
	private readonly TrackedMap<bool> _values = new(comparer, changed);
	public bool Contains(string key) => _values.ContainsKey(key);
	public bool Add(string key) => _values.TryAdd(key, true);
	public bool Remove(string key) => _values.Remove(key);
}
