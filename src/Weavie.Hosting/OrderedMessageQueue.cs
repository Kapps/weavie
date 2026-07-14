using System.Collections.Concurrent;

namespace Weavie.Hosting;

internal sealed class OrderedMessageQueue(Action<Action> schedule, Action<string> send) : IDisposable {
	private readonly ConcurrentQueue<string> _messages = new();
	private readonly object _lifecycle = new();
	private int _scheduled;
	private int _closed;

	public void Enqueue(string message) {
		if (Volatile.Read(ref _closed) != 0) {
			return;
		}

		_messages.Enqueue(message);
		if (Volatile.Read(ref _closed) != 0) {
			_messages.Clear();
			return;
		}

		if (Volatile.Read(ref _scheduled) == 0) {
			lock (_lifecycle) {
				if (_closed != 0) {
					_messages.Clear();
					return;
				}

				if (Interlocked.CompareExchange(ref _scheduled, 1, 0) == 0) {
					schedule(Drain);
				}
			}
		}
	}

	private void Drain() {
		while (true) {
			while (_messages.TryDequeue(out string? message)) {
				lock (_lifecycle) {
					if (_closed != 0) {
						_messages.Clear();
						return;
					}

					send(message);
				}
			}

			Interlocked.Exchange(ref _scheduled, 0);
			if (_messages.IsEmpty || Interlocked.CompareExchange(ref _scheduled, 1, 0) != 0) {
				return;
			}
		}
	}

	public void Dispose() {
		lock (_lifecycle) {
			Volatile.Write(ref _closed, 1);
			_messages.Clear();
		}
	}
}
