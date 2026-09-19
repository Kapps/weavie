using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Hosting;
using ZstdSharp;

namespace Weavie.Headless;

internal sealed partial class WebSocketHostBridge {
	internal const int MaxWireMessageBytes = 768 * 1024;
	private const int ChunkPayloadCharacters = 64 * 1024;
	private const int RawMessageCharacters = MaxWireMessageBytes / 3;
	private static readonly JsonSerializerOptions ChunkJsonOptions = new(JsonSerializerDefaults.Web);

	private sealed record OutboundMessage(WebMessageRoute Route, byte[][] Frames) {
		public bool UsesLargeLane => Frames.Length > 1;

		public int Weight => Math.Min(Frames.Sum(frame => frame.Length), OutboxByteCapacity);
	}

	private sealed class PendingMessage(OutboundMessage message) {
		private int _index;

		public bool Complete => _index == message.Frames.Length;

		public bool UsesLargeLane => message.UsesLargeLane;

		public int Weight => message.Weight;

		public byte[] Next() => message.Frames[_index++];
	}

	private static byte[][] EncodeFrames(string json, string id) {
		using var compressor = new Compressor(3);
		if (json.Length <= RawMessageCharacters) {
			return [compressor.Wrap(Encoding.UTF8.GetBytes(json)).ToArray()];
		}

		var ranges = new List<ChunkRange>();
		for (int offset = 0; offset < json.Length;) {
			int length = Math.Min(ChunkPayloadCharacters, json.Length - offset);
			if (offset + length < json.Length && char.IsHighSurrogate(json[offset + length - 1])) length--;
			ranges.Add(new ChunkRange(offset, length));
			offset += length;
		}

		return [.. ranges.Select((range, index) => {
			var characters = json.AsSpan(range.Offset, range.Length);
			byte[] payload = new byte[Encoding.UTF8.GetByteCount(characters)];
			Encoding.UTF8.GetBytes(characters, payload);
			byte[] wire = JsonSerializer.SerializeToUtf8Bytes(new ChunkWire(new ChunkBody(
				id, index, ranges.Count, Convert.ToBase64String(payload))), ChunkJsonOptions);
			return compressor.Wrap(wire).ToArray();
		})];
	}

	private sealed class FairOutbox(int capacity, int byteCapacity) {
		private readonly object _gate = new();
		private readonly Dictionary<WebMessageRoute, Queue<PendingMessage>> _pending = [];
		private readonly Queue<WebMessageRoute> _routes = [];
		private TaskCompletionSource _changed = NewSignal();
		private WebMessageRoute? _largeRoute;
		private int _bytes;
		private bool _closed;
		private int _messages;

		public bool TryWrite(OutboundMessage message) {
			lock (_gate) {
				if (_closed
					|| _messages >= capacity
					|| message.Weight > byteCapacity - _bytes) {
					return false;
				}
				bool addedRoute = false;
				if (!_pending.TryGetValue(message.Route, out var queue)) {
					queue = new Queue<PendingMessage>();
					_pending.Add(message.Route, queue);
					_routes.Enqueue(message.Route);
					addedRoute = true;
				}
				queue.Enqueue(new PendingMessage(message));
				_messages++;
				_bytes += message.Weight;
				if (addedRoute) {
					PulseLocked();
				}
				return true;
			}
		}

		public async ValueTask<OutboundTurn?> NextAsync() {
			while (true) {
				Task changed;
				lock (_gate) {
					int candidates = _routes.Count;
					while (candidates-- > 0 && _routes.TryDequeue(out var route)) {
						var message = _pending[route].Peek();
						if (message.UsesLargeLane && _largeRoute is { } active && active != route) {
							_routes.Enqueue(route);
							continue;
						}
						if (message.UsesLargeLane) {
							_largeRoute = route;
						}
						byte[] bytes = message.Next();
						return new OutboundTurn(route, bytes, message.Complete);
					}
					if (_closed) {
						return null;
					}
					changed = _changed.Task;
				}
				await changed.ConfigureAwait(false);
			}
		}

		public void CompleteTurn(OutboundTurn turn) {
			lock (_gate) {
				var queue = _pending[turn.Route];
				if (turn.CompletesMessage) {
					var completed = queue.Dequeue();
					_messages--;
					_bytes -= completed.Weight;
					if (completed.UsesLargeLane) {
						_largeRoute = null;
					}
				}
				if (queue.Count == 0) {
					_pending.Remove(turn.Route);
				} else {
					_routes.Enqueue(turn.Route);
				}
				PulseLocked();
			}
		}

		public void Complete() {
			lock (_gate) {
				_closed = true;
				PulseLocked();
			}
		}

		private void PulseLocked() {
			var changed = _changed;
			_changed = NewSignal();
			changed.TrySetResult();
		}

		private static TaskCompletionSource NewSignal() =>
			new(TaskCreationOptions.RunContinuationsAsynchronously);
	}

	private sealed record OutboundTurn(
		WebMessageRoute Route,
		byte[] Bytes,
		bool CompletesMessage);

	private sealed record ChunkRange(int Offset, int Length);

	private sealed record ChunkWire(
		[property: JsonPropertyName("$weavieChunk")] ChunkBody Chunk);

	private sealed record ChunkBody(string Id, int Index, int Count, string Data);
}
