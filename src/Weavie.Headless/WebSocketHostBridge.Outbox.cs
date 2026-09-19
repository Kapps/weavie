using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Hosting;
using ZstdSharp;

namespace Weavie.Headless;

internal sealed partial class WebSocketHostBridge {
	internal const int MaxWireMessageBytes = 768 * 1024;
	private const int ChunkPayloadBytes = 64 * 1024;
	private const int RawMessageCharacters = MaxWireMessageBytes / 3;
	private static readonly JsonSerializerOptions ChunkJsonOptions = new(JsonSerializerDefaults.Web);

	private sealed record OutboundMessage(WebMessageRoute Route, string Json, string Id) {
		private readonly Lazy<byte[]> _compressed = new(() => {
			using var compressor = new Compressor(3);
			return compressor.Wrap(Encoding.UTF8.GetBytes(Json)).ToArray();
		});

		public byte[] Compressed => _compressed.Value;

		public bool UsesLargeLane => Json.Length > RawMessageCharacters;

		public int Weight => Math.Min(Json.Length, OutboxCharacterCapacity);
	}

	private sealed class PendingMessage(OutboundMessage message) {
		public TaskCompletionSource Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private int _index;
		private int _count;

		public bool Complete => message.UsesLargeLane
			? _count > 0 && _index == _count
			: _index == 1;

		public bool UsesLargeLane => message.UsesLargeLane;

		public int Weight => message.Weight;

		public byte[] Next() {
			if (!message.UsesLargeLane) {
				_index = 1;
				return Encoding.UTF8.GetBytes(message.Json);
			}

			byte[] compressed = message.Compressed;
			_count = (compressed.Length + ChunkPayloadBytes - 1) / ChunkPayloadBytes;
			int offset = _index * ChunkPayloadBytes;
			return JsonSerializer.SerializeToUtf8Bytes(new ChunkWire(new ChunkBody(
				message.Id,
				_index++,
				_count,
				Convert.ToBase64String(compressed, offset, Math.Min(ChunkPayloadBytes, compressed.Length - offset)))), ChunkJsonOptions);
		}
	}

	private sealed class FairOutbox(int capacity, int characterCapacity) {
		private readonly object _gate = new();
		private readonly Dictionary<WebMessageRoute, Queue<PendingMessage>> _pending = [];
		private readonly Queue<WebMessageRoute> _routes = [];
		private TaskCompletionSource _changed = NewSignal();
		private WebMessageRoute? _largeRoute;
		private int _characters;
		private bool _closed;
		private int _messages;

		public bool TryWrite(OutboundMessage message) {
			lock (_gate) {
				if (_closed
					|| _messages >= capacity
					|| message.Weight > characterCapacity - _characters) {
					return false;
				}
				EnqueueLocked(message.Route, new PendingMessage(message));
				return true;
			}
		}

		public async Task WriteAsync(OutboundMessage message, CancellationToken cancellationToken) {
			cancellationToken.ThrowIfCancellationRequested();
			PendingMessage pending;
			while (true) {
				Task changed;
				lock (_gate) {
					if (_closed) return;
					if (_messages < capacity && message.Weight <= characterCapacity - _characters) {
						pending = new PendingMessage(message);
						EnqueueLocked(message.Route, pending);
						break;
					}
					changed = _changed.Task;
				}
				await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
			}
			await pending.Sent.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
		}

		private void EnqueueLocked(WebMessageRoute route, PendingMessage pending) {
			bool addedRoute = false;
			if (!_pending.TryGetValue(route, out var queue)) {
				queue = new Queue<PendingMessage>();
				_pending.Add(route, queue);
				_routes.Enqueue(route);
				addedRoute = true;
			}
			queue.Enqueue(pending);
			_messages++;
			_characters += pending.Weight;
			if (addedRoute) PulseLocked();
		}

		public async ValueTask<OutboundTurn?> NextAsync() {
			while (true) {
				Task changed;
				PendingMessage? selected = null;
				WebMessageRoute selectedRoute = default;
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
						selected = message;
						selectedRoute = route;
						break;
					}
					if (selected is null && _closed) {
						return null;
					}
					changed = _changed.Task;
				}
				if (selected is not null) {
					byte[] bytes = selected.Next();
					return new OutboundTurn(selectedRoute, bytes, selected.Complete);
				}
				await changed.ConfigureAwait(false);
			}
		}

		public void CompleteTurn(OutboundTurn turn) {
			lock (_gate) {
				var queue = _pending[turn.Route];
				if (turn.CompletesMessage) {
					var completed = queue.Dequeue();
					completed.Sent.TrySetResult();
					_messages--;
					_characters -= completed.Weight;
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
				foreach (var pending in _pending.Values.SelectMany(queue => queue)) pending.Sent.TrySetResult();
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

	private sealed record ChunkWire(
		[property: JsonPropertyName("$weavieChunk")] ChunkBody Chunk);

	private sealed record ChunkBody(string Id, int Index, int Count, string Data);
}
