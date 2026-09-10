using Weavie.Core.FileActivity;
using Weavie.Core.FileSystem;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class WorkspaceFileIndexPublisherTests : IDisposable {
	private readonly TempDirectory _root = new("weavie-index-publication");

	[Fact]
	public async Task InitialPublicationWaitsForObservationAndIncludesFilesCreatedBeforeWatcherStart() {
		int loads = 0;
		_root.WriteFile("a.ts", "");
		var inventory = new WorkspaceInventory(_root.Path, _ => {
			Interlocked.Increment(ref loads);
			return Task.FromResult<IReadOnlyList<string>?>(Directory.GetFiles(_root.Path));
		});
		await inventory.RefreshAsync();
		Assert.Equal([PathOf("a.ts")], inventory.LastSnapshot!.Files);
		_root.WriteFile("b.ts", "");
		using var watcher = new WorkspaceInvalidationWatcher(inventory, _ => { }, _ => { }, 1);
		using var publisher = new WorkspaceFileIndexPublisher(
			inventory, new WorkspaceFileIndex(new InMemoryFileSystem(), _root.Path), watcher);
		var publications = new List<string[]>();
		var publishing = publisher.PublishCurrentAsync(files => publications.Add([.. files]), UnexpectedFailure, CancellationToken.None);
		Assert.False(publishing.IsCompleted);
		Assert.Empty(publications);

		var run = watcher.RunAsync(CancellationToken.None);
		try {
			await watcher.Ready;
			await publishing;
			Assert.Equal(new[] { PathOf("a.ts"), PathOf("b.ts") }, Assert.Single(publications));
			Assert.Equal(3, loads); // Initial inventory, watcher installation, then watcher reconciliation.
		} finally {
			await watcher.StopAsync();
			await run;
		}
	}

	[Fact]
	public async Task PublicationOrderIncludesDeliveryOfTheEarlierSnapshot() {
		IReadOnlyList<string> files = ["a.ts"];
		var inventory = new WorkspaceInventory(_root.Path, _ => Task.FromResult<IReadOnlyList<string>?>(files));
		await inventory.RefreshAsync();
		using var publisher = NewPublisher(inventory, Task.CompletedTask);
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var publications = new List<string[]>();
		var first = Task.Run(() => publisher.PublishCurrentAsync(snapshot => {
			entered.SetResult();
			release.Task.GetAwaiter().GetResult();
			publications.Add([.. snapshot]);
		}, UnexpectedFailure, CancellationToken.None));
		await entered.Task;
		Task second;
		try {
			files = ["a.ts", "b.ts"];
			await inventory.RefreshAsync();
			second = publisher.PublishCurrentAsync(snapshot => publications.Add([.. snapshot]), UnexpectedFailure, CancellationToken.None);
			Assert.False(second.IsCompleted);
			Assert.Empty(publications);
		} finally {
			release.SetResult();
		}
		await Task.WhenAll(first, second);

		Assert.Collection(publications,
			snapshot => Assert.Equal([PathOf("a.ts")], snapshot),
			snapshot => Assert.Equal([PathOf("a.ts"), PathOf("b.ts")], snapshot));
	}

	[Fact]
	public async Task CancellationBeforeObservationPublishesNothing() {
		var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var inventory = new WorkspaceInventory(_root.Path, _ => throw new InvalidOperationException("Unexpected refresh"));
		using var publisher = NewPublisher(inventory, ready.Task);
		using var cancellation = new CancellationTokenSource();
		var publications = new List<IReadOnlyList<string>>();
		var publishing = publisher.PublishCurrentAsync(publications.Add, UnexpectedFailure, cancellation.Token);
		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publishing);
		Assert.Empty(publications);
	}

	[Fact]
	public async Task FailedObservationPublishesNothing() {
		var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var inventory = new WorkspaceInventory(_root.Path, _ => throw new InvalidOperationException("Unexpected refresh"));
		using var publisher = NewPublisher(inventory, ready.Task);
		var publications = new List<IReadOnlyList<string>>();
		var failures = new List<Exception>();
		var publishing = publisher.PublishCurrentAsync(publications.Add, failures.Add, CancellationToken.None);
		var failure = new IOException("Cannot observe workspace");
		ready.SetException(failure);

		await publishing;
		Assert.Same(failure, Assert.Single(failures));
		Assert.Empty(publications);
	}

	[Fact]
	public async Task NonRepositoryPublicationSeedsTheAuthoritativeInventoryFromNavigation() {
		var inventory = new WorkspaceInventory(_root.Path, _ => Task.FromResult<IReadOnlyList<string>?>(null));
		await inventory.RefreshAsync();
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(PathOf("src", "a.ts"), "");
		fs.WriteAllText(PathOf("b.ts"), "");
		using var publisher = new WorkspaceFileIndexPublisher(
			inventory, new WorkspaceFileIndex(fs, _root.Path), new ControlledObserver(Task.CompletedTask));
		var publications = new List<IReadOnlyList<string>>();

		await publisher.PublishCurrentAsync(publications.Add, UnexpectedFailure, CancellationToken.None);

		string[] expected = [PathOf("b.ts"), PathOf("src", "a.ts")];
		Assert.Equal(expected, Assert.Single(publications));
		var seeded = await inventory.RefreshAsync();
		Assert.Equal(expected, seeded.Files);
		Assert.Contains(PathOf("src"), seeded.Directories);
	}

	[Fact]
	public async Task FailedRefreshDeliveryCannotOverwriteANewerPublication() {
		bool fail = false;
		IReadOnlyList<string> files = ["a.ts"];
		var failure = new IOException("Inventory unavailable");
		var inventory = new WorkspaceInventory(_root.Path, _ => fail
			? Task.FromException<IReadOnlyList<string>?>(failure)
			: Task.FromResult<IReadOnlyList<string>?>(files));
		await inventory.RefreshAsync();
		using var publisher = NewPublisher(inventory, Task.CompletedTask);
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var deliveries = new List<string>();
		fail = true;
		var first = Task.Run(() => publisher.RefreshAndPublishAsync(
			_ => Assert.Fail("Failed refresh must not publish files"), error => {
				Assert.Same(failure, error);
				entered.SetResult();
				release.Task.GetAwaiter().GetResult();
				deliveries.Add("failure");
			}, CancellationToken.None));
		await entered.Task;
		Task second;
		try {
			fail = false;
			files = ["a.ts", "b.ts"];
			await inventory.RefreshAsync();
			second = publisher.PublishCurrentAsync(snapshot => {
				Assert.Equal([PathOf("a.ts"), PathOf("b.ts")], snapshot);
				deliveries.Add("success");
			}, UnexpectedFailure, CancellationToken.None);
			Assert.False(second.IsCompleted);
			Assert.Empty(deliveries);
		} finally {
			release.SetResult();
		}
		await Task.WhenAll(first, second);
		Assert.Equal(["failure", "success"], deliveries);
	}

	private string PathOf(params string[] segments) => WorkspacePaths.CanonicalFsPath(_root.Combine(segments));

	private static void UnexpectedFailure(Exception error) => Assert.Fail(error.ToString());

	private WorkspaceFileIndexPublisher NewPublisher(WorkspaceInventory inventory, Task ready) =>
		new(inventory, new WorkspaceFileIndex(new InMemoryFileSystem(), _root.Path), new ControlledObserver(ready));

	private sealed class ControlledObserver(Task ready) : IWorkspaceNavigationObserver, IWorkspaceNavigationObservation {
		public Task ObservationReady => ready;

		public Task<IWorkspaceNavigationObservation> ObserveNavigationAsync(CancellationToken ct) {
			ct.ThrowIfCancellationRequested();
			return Task.FromResult<IWorkspaceNavigationObservation>(this);
		}

		public void ObserveDirectory(string path) { }

		public void Dispose() { }
	}

	public void Dispose() => _root.Dispose();
}
