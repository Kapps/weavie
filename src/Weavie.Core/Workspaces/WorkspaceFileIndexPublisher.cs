using Weavie.Core.FileActivity;
using Weavie.Core.Git;

namespace Weavie.Core.Workspaces;

/// <summary>Orders file-index publication after the observer establishes its initial inventory.</summary>
public sealed class WorkspaceFileIndexPublisher : IDisposable {
	private readonly WorkspaceInventory _inventory;
	private readonly WorkspaceFileIndex _navigation;
	private readonly IWorkspaceNavigationObserver _observer;
	private readonly SemaphoreSlim _gate = new(1, 1);

	/// <summary>Owns publication over the inventory established by the workspace observer.</summary>
	public WorkspaceFileIndexPublisher(
		WorkspaceInventory inventory,
		WorkspaceFileIndex navigation,
		IWorkspaceNavigationObserver observer) {
		ArgumentNullException.ThrowIfNull(inventory);
		ArgumentNullException.ThrowIfNull(navigation);
		ArgumentNullException.ThrowIfNull(observer);
		_inventory = inventory;
		_navigation = navigation;
		_observer = observer;
	}

	/// <summary>Publishes the observer's current inventory without reloading Git.</summary>
	public Task PublishCurrentAsync(Action<IReadOnlyList<string>> publish, Action<Exception> publishFailure, CancellationToken ct) =>
		PublishAsync(publish, publishFailure, refresh: false, ct);

	/// <summary>Reloads the inventory and publishes it in the same ordered operation.</summary>
	public Task RefreshAndPublishAsync(Action<IReadOnlyList<string>> publish, Action<Exception> publishFailure, CancellationToken ct) =>
		PublishAsync(publish, publishFailure, refresh: true, ct);

	private async Task PublishAsync(
		Action<IReadOnlyList<string>> publish,
		Action<Exception> publishFailure,
		bool refresh,
		CancellationToken ct) {
		await _gate.WaitAsync(ct).ConfigureAwait(false);
		try {
			IReadOnlyList<string> files;
			try {
				await _observer.ObservationReady.WaitAsync(ct).ConfigureAwait(false);
				var snapshot = refresh
					? await _inventory.RefreshAsync(ct).ConfigureAwait(false)
					: _inventory.LastSnapshot ?? throw new InvalidOperationException("Workspace observation has no inventory.");
				files = snapshot.IsRepository ? snapshot.Files : await SeedNavigationAsync(ct).ConfigureAwait(false);
			} catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException) {
				ct.ThrowIfCancellationRequested();
				publishFailure(ex);
				return;
			}
			ct.ThrowIfCancellationRequested();
			publish(files);
		} finally {
			_gate.Release();
		}
	}

	private async Task<IReadOnlyList<string>> SeedNavigationAsync(CancellationToken ct) {
		using var observation = await _observer.ObserveNavigationAsync(ct).ConfigureAwait(false);
		var seed = await _inventory.BeginNonRepositorySeedAsync(ct).ConfigureAwait(false);
		bool completed = false;
		try {
			var navigation = _navigation.ListSnapshot(observation.ObserveDirectory);
			var inventory = _inventory.CompleteNonRepositorySeed(seed, navigation.Files, navigation.Directories);
			completed = true;
			return inventory.Files;
		} finally {
			if (!completed) _inventory.CancelNonRepositorySeed(seed);
		}
	}

	/// <inheritdoc/>
	public void Dispose() => _gate.Dispose();
}
