namespace Weavie.Hosting;

internal sealed class ReviewPublication : IDisposable {
	private readonly SemaphoreSlim _gate = new(1, 1);

	public Task RunAsync(Func<Task> publish, CancellationToken ct) => RunAsync(async () => {
		await publish().ConfigureAwait(false);
		return true;
	}, ct);

	public async Task<T> RunAsync<T>(Func<Task<T>> publish, CancellationToken ct) {
		await _gate.WaitAsync(ct).ConfigureAwait(false);
		try {
			return await publish().ConfigureAwait(false);
		} finally {
			_gate.Release();
		}
	}

	public void Dispose() => _gate.Dispose();
}
