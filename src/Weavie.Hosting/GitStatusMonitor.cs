using System.Threading.Channels;

namespace Weavie.Hosting;

/// <summary>Owns one session incarnation's serialized, coalesced Git-status refreshes.</summary>
internal sealed class GitStatusMonitor {
	private readonly Lock _snapshotGate = new();
	private readonly Channel<bool> _signals = Channel.CreateUnbounded<bool>(
		new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
	private readonly Func<CancellationToken, Task<GitStatusSnapshot>> _resolve;
	private readonly Action<GitStatusSnapshot> _publish;
	private readonly TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

	public GitStatusMonitor(
		SessionTaskScope background,
		Func<CancellationToken, Task<GitStatusSnapshot>> resolve,
		Action<GitStatusSnapshot> publish) {
		ArgumentNullException.ThrowIfNull(background);
		ArgumentNullException.ThrowIfNull(resolve);
		ArgumentNullException.ThrowIfNull(publish);

		_resolve = resolve;
		_publish = publish;
		_ = background.Run(RunAsync);
	}

	public GitStatusSnapshot? Latest {
		get {
			lock (_snapshotGate) {
				return field;
			}
		}

		private set;
	}

	public void RequestRefresh() => _signals.Writer.TryWrite(true);

	internal Task Waiting => _waiting.Task;

	private async Task RunAsync(CancellationToken ct) {
		_waiting.TrySetResult();
		while (await _signals.Reader.WaitToReadAsync(ct).ConfigureAwait(false)) {
			while (_signals.Reader.TryRead(out _)) {
			}

			var snapshot = await _resolve(ct).ConfigureAwait(false);
			bool changed;
			lock (_snapshotGate) {
				changed = !Equals(Latest, snapshot);
				Latest = snapshot;
			}

			if (changed) {
				_publish(snapshot);
			}
		}
	}
}

internal sealed record GitStatusSnapshot(
	string? Branch,
	bool Dirty,
	int? Added,
	int? Removed,
	string? Error);
