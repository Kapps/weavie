using Weavie.Core.FileActivity;

namespace Weavie.Hosting;

internal sealed class DirectoryWatchSubscriptions(ObservedPathWatcher watcher) : IDisposable {
	private readonly string _scope = Guid.NewGuid().ToString("N");
	private readonly HashSet<string> _subscriptions = new(StringComparer.Ordinal);
	private readonly Lock _gate = new();
	private string? _pageEpoch;
	private bool _disposed;

	public void Watch(string subscriptionId, string path) {
		lock (_gate) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			watcher.WatchDirectory(Key(subscriptionId), path);
			_subscriptions.Add(subscriptionId);
		}
	}

	public void Unwatch(string subscriptionId) {
		lock (_gate) {
			if (_subscriptions.Remove(subscriptionId)) {
				watcher.UnwatchDirectory(Key(subscriptionId));
			}
		}
	}

	public void Reset(string pageEpoch) {
		ArgumentException.ThrowIfNullOrEmpty(pageEpoch);
		lock (_gate) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_pageEpoch == pageEpoch) {
				return;
			}

			ReleaseAll();
			_pageEpoch = pageEpoch;
		}
	}

	public void Dispose() {
		lock (_gate) {
			_disposed = true;
			ReleaseAll();
		}
	}

	private void ReleaseAll() {
		foreach (string id in _subscriptions) {
			watcher.UnwatchDirectory(Key(id));
		}
		_subscriptions.Clear();
	}

	private string Key(string subscriptionId) {
		ArgumentException.ThrowIfNullOrEmpty(subscriptionId);
		return $"{_scope}:{subscriptionId}";
	}
}
