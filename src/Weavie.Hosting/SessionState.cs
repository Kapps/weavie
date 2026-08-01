using System.Text.Json;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

internal sealed class SessionState {
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
	private readonly SessionMessageBus _bus;
	private readonly Lock _gate = new();
	private readonly Dictionary<(string Feature, string Key), Entry> _entries = [];
	private long _sequence;

	public SessionState(SessionMessageBus bus) {
		ArgumentNullException.ThrowIfNull(bus);
		_bus = bus;
	}

	public void Set<T>(
		string feature,
		string key,
		string name,
		T payload) {
		ArgumentException.ThrowIfNullOrEmpty(feature);
		ArgumentException.ThrowIfNullOrEmpty(key);
		ArgumentException.ThrowIfNullOrEmpty(name);
		string json = JsonSerializer.Serialize(payload, JsonOptions);
		lock (_gate) {
			_entries[(feature, key)] = new Entry(++_sequence, name, json);
			_bus.Feature(feature).PublishJson(name, json);
		}
	}

	public void Remove(string feature, string key) {
		ArgumentException.ThrowIfNullOrEmpty(feature);
		ArgumentException.ThrowIfNullOrEmpty(key);
		lock (_gate) {
			_entries.Remove((feature, key));
		}
	}

	public void Replay(MessageTarget target) {
		ArgumentNullException.ThrowIfNull(target);
		lock (_gate) {
			foreach (var entry in _entries
				.OrderBy(candidate => candidate.Value.Sequence)) {
				target.Feature(entry.Key.Feature).PublishJson(
					entry.Value.Name,
					entry.Value.Json);
			}
		}
	}

	private sealed record Entry(long Sequence, string Name, string Json);
}
