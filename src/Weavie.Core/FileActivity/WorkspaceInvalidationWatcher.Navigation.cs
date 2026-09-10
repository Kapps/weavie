namespace Weavie.Core.FileActivity;

public sealed partial class WorkspaceInvalidationWatcher {
	private readonly SemaphoreSlim _observationGate = new(1, 1);
	private bool _observationEnded;

	Task IWorkspaceNavigationObserver.ObservationReady => Ready;

	/// <summary>Holds observation stable while navigation seeds the inventory.</summary>
	public async Task<IWorkspaceNavigationObservation> ObserveNavigationAsync(CancellationToken ct) {
		var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
		try {
			await Ready.WaitAsync(cancellation.Token).ConfigureAwait(false);
			await _observationGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
			if (cancellation.IsCancellationRequested || _observationEnded) {
				_observationGate.Release();
				cancellation.Token.ThrowIfCancellationRequested();
				throw new IOException("Workspace observation has stopped.");
			}
			return new NavigationObservation(this, cancellation);
		} catch {
			cancellation.Dispose();
			throw;
		}
	}

	private async Task DisposeWatchesAsync() {
		await _observationGate.WaitAsync().ConfigureAwait(false);
		try {
			_observationEnded = true;
			_directoryWatchers.Dispose();
		} finally {
			_observationGate.Release();
		}
	}

	private sealed class NavigationObservation(WorkspaceInvalidationWatcher owner, CancellationTokenSource cancellation) : IWorkspaceNavigationObservation {
		private bool _disposed;

		public void ObserveDirectory(string path) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			cancellation.Token.ThrowIfCancellationRequested();
			owner._directoryWatchers.EnsureWatching(path);
		}

		public void Dispose() {
			if (_disposed) return;
			_disposed = true;
			cancellation.Dispose();
			owner._observationGate.Release();
		}
	}
}
