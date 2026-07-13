using System.Collections.Concurrent;

namespace Weavie.Hosting;

internal sealed class OrderedMessageQueue(Action<Action> schedule, Action<string> send) {
	private readonly ConcurrentQueue<string> _messages = new();
	private int _scheduled;

	public void Enqueue(string message) {
		_messages.Enqueue(message);
		if (Interlocked.CompareExchange(ref _scheduled, 1, 0) == 0) {
			schedule(Drain);
		}
	}

	private void Drain() {
		while (true) {
			while (_messages.TryDequeue(out string? message)) {
				send(message);
			}

			Interlocked.Exchange(ref _scheduled, 0);
			if (_messages.IsEmpty || Interlocked.CompareExchange(ref _scheduled, 1, 0) != 0) {
				return;
			}
		}
	}
}
