using System.Collections.Concurrent;
using System.Reflection;
using Weavie.Core.FileActivity;
using Weavie.Core.FileSystem;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class WorkspaceNavigationObservationTests : IDisposable {
	private readonly TempDirectory _root = new("weavie-watched-navigation");

	[Fact]
	public async Task PublicationIncludesFileCreatedBeforeNestedWatchIsArmed() {
		string nested = _root.CreateDirectory("nested");
		string created = Path.Combine(nested, "created.txt");
		var inventory = NewInventory();
		using var watcher = NewWatcher(inventory, _ => { }, path => {
			if (PathIdentity.Equals(path, nested)) File.WriteAllText(created, "before arming");
			return new FileSystemWatcher(path);
		});
		var run = watcher.RunAsync(CancellationToken.None);
		try {
			await watcher.Ready;
			using var publisher = NewPublisher(inventory, watcher, new LocalFileSystem());
			var publications = new List<IReadOnlyList<string>>();

			await publisher.PublishCurrentAsync(publications.Add, UnexpectedFailure, CancellationToken.None);

			Assert.True(File.Exists(created));
			Assert.Equal([Canonical(created)], Assert.Single(publications));
			Assert.Equal([Canonical(created)], (await inventory.RefreshAsync()).Files);
		} finally {
			await watcher.StopAsync();
			await run;
		}
	}

	[Fact]
	public async Task DeletionDuringWatchedEnumerationCannotResurrectAStaleEntry() {
		string deleted = _root.WriteFile("deleted.txt", "");
		var delivered = NewSignal();
		var inventory = NewInventory();
		using var watcher = NewWatcher(inventory, batch => {
			if (batch.Any(change => PathIdentity.Equals(change.Path, deleted) && change.Kind == FileInvalidationKind.Deleted)) delivered.TrySetResult();
		}, path => new FileSystemWatcher(path));
		var run = watcher.RunAsync(CancellationToken.None);
		bool deleteDuringEnumeration = false;
		var fileSystem = new EnumeratingFileSystem(path => {
			if (!deleteDuringEnumeration || !PathIdentity.Equals(path, _root.Path)) return;
			deleteDuringEnumeration = false;
			File.Delete(deleted);
			delivered.Task.GetAwaiter().GetResult();
		});
		try {
			await watcher.Ready;
			using var publisher = NewPublisher(inventory, watcher, fileSystem);
			var publications = new List<IReadOnlyList<string>>();
			await publisher.PublishCurrentAsync(publications.Add, UnexpectedFailure, CancellationToken.None);
			Assert.Equal([Canonical(deleted)], Assert.Single(publications));
			deleteDuringEnumeration = true;

			await publisher.PublishCurrentAsync(publications.Add, UnexpectedFailure, CancellationToken.None);

			Assert.True(delivered.Task.IsCompletedSuccessfully);
			Assert.Empty(publications[1]);
			Assert.Empty((await inventory.RefreshAsync()).Files);
		} finally {
			await watcher.StopAsync();
			await run;
		}
	}

	[Fact]
	public async Task PopulatedDirectoryMovedIntoWorkspaceInvalidatesNavigationAndPublishesDescendants() {
		using var outside = new TempDirectory("weavie-incoming-navigation");
		string source = outside.CreateDirectory("incoming");
		outside.WriteFile("incoming/nested/inside.txt", "");
		string destination = _root.Combine("incoming");
		var delivered = NewSignal();
		var inventory = NewInventory();
		using var watcher = NewWatcher(inventory, batch => {
			if (batch.Any(change => PathIdentity.Equals(change.Path, destination) && change.Kind == FileInvalidationKind.Created)) delivered.TrySetResult();
		}, path => new FileSystemWatcher(path));
		var run = watcher.RunAsync(CancellationToken.None);
		try {
			await watcher.Ready;
			Directory.Move(source, destination);
			await delivered.Task;
			using var publisher = NewPublisher(inventory, watcher, new LocalFileSystem());
			var publications = new List<IReadOnlyList<string>>();

			await publisher.PublishCurrentAsync(publications.Add, UnexpectedFailure, CancellationToken.None);

			Assert.Equal([Canonical(Path.Combine(destination, "nested", "inside.txt"))], Assert.Single(publications));
		} finally {
			await watcher.StopAsync();
			await run;
		}
	}

	[Fact]
	public async Task StopWaitsForNavigationToReleaseItsWatches() {
		string nested = _root.CreateDirectory("nested");
		var handles = new ConcurrentDictionary<string, FileSystemWatcher>(PathIdentity.Comparer);
		using var watcher = NewWatcher(NewInventory(), _ => { }, path => {
			var handle = new FileSystemWatcher(path);
			handles[path] = handle;
			return handle;
		});
		var run = watcher.RunAsync(CancellationToken.None);
		await watcher.Ready;
		using var observation = await watcher.ObserveNavigationAsync(CancellationToken.None);
		observation.ObserveDirectory(nested);
		var stop = watcher.StopAsync();
		try {
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watcher.ObserveNavigationAsync(CancellationToken.None));
			Assert.False(stop.IsCompleted);
			Assert.True(handles[nested].EnableRaisingEvents);
			Assert.ThrowsAny<OperationCanceledException>(() => observation.ObserveDirectory(nested));
		} finally {
			observation.Dispose();
		}
		await stop;
		await run;
		Assert.False(handles[nested].EnableRaisingEvents);
	}

	[Fact]
	public async Task ReconciliationCannotRemoveAWatchedDirectoryUntilNavigationCompletes() {
		string nested = _root.CreateDirectory("nested");
		var handles = new ConcurrentDictionary<string, FileSystemWatcher>(PathIdentity.Comparer);
		using var watcher = NewWatcher(NewInventory(), _ => { }, path => {
			var handle = new FileSystemWatcher(path);
			handles[path] = handle;
			return handle;
		});
		var run = watcher.RunAsync(CancellationToken.None);
		try {
			await watcher.Ready;
			using var observation = await watcher.ObserveNavigationAsync(CancellationToken.None);
			observation.ObserveDirectory(nested);
			// Invoke the actual reconciliation to establish its blocked boundary without timer scheduling.
			var refresh = (Task)typeof(WorkspaceInvalidationWatcher)
				.GetMethod("RefreshAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
				.Invoke(watcher, [false, CancellationToken.None])!;
			try {
				Assert.False(refresh.IsCompleted);
				Assert.True(handles[nested].EnableRaisingEvents);
			} finally {
				observation.Dispose();
			}
			await refresh;
			Assert.False(handles[nested].EnableRaisingEvents);
		} finally {
			await watcher.StopAsync();
			await run;
		}
	}

	[Fact]
	public async Task CallerCancellationStopsNavigationAndReleasesItsLeaseOnDisposal() {
		using var watcher = NewWatcher(NewInventory(), _ => { }, path => new FileSystemWatcher(path));
		var run = watcher.RunAsync(CancellationToken.None);
		try {
			await watcher.Ready;
			using var cancellation = new CancellationTokenSource();
			using var observation = await watcher.ObserveNavigationAsync(cancellation.Token);
			cancellation.Cancel();
			Assert.ThrowsAny<OperationCanceledException>(() => observation.ObserveDirectory(_root.Path));
			observation.Dispose();
			using var next = await watcher.ObserveNavigationAsync(CancellationToken.None);
			next.ObserveDirectory(_root.Path);
		} finally {
			await watcher.StopAsync();
			await run;
		}
	}

	private WorkspaceInventory NewInventory() => new(_root.Path, _ => Task.FromResult<IReadOnlyList<string>?>(null));

	private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

	private static string Canonical(string path) => WorkspacePaths.CanonicalFsPath(path);

	private static void UnexpectedFailure(Exception error) => Assert.Fail(error.ToString());

	private WorkspaceFileIndexPublisher NewPublisher(WorkspaceInventory inventory, WorkspaceInvalidationWatcher watcher, IFileSystem fileSystem) =>
		new(inventory, new WorkspaceFileIndex(fileSystem, _root.Path), watcher);

	private static WorkspaceInvalidationWatcher NewWatcher(
		WorkspaceInventory inventory, Action<IReadOnlyList<FileInvalidation>> onChanges, Func<string, FileSystemWatcher> create) =>
		new(inventory, onChanges, _ => { }, 0, (_, _) => Task.CompletedTask, create);

	public void Dispose() => _root.Dispose();

	private sealed class EnumeratingFileSystem(Action<string> afterEnumeration) : IFileSystem {
		private readonly LocalFileSystem _inner = new();

		public IReadOnlyList<DirectoryEntry> EnumerateDirectory(string path) {
			var entries = _inner.EnumerateDirectory(path);
			afterEnumeration(path);
			return entries;
		}

		public bool FileExists(string path) => _inner.FileExists(path);
		public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
		public bool TryGetStat(string path, out FileStat stat) => _inner.TryGetStat(path, out stat);
		public string ReadAllText(string path) => _inner.ReadAllText(path);
		public bool TryReadAllText(string path, out string contents) => _inner.TryReadAllText(path, out contents);
		public byte[] ReadAllBytes(string path) => _inner.ReadAllBytes(path);
		public void WriteAllText(string path, string contents) => _inner.WriteAllText(path, contents);
		public void WriteAllBytes(string path, byte[] contents) => _inner.WriteAllBytes(path, contents);
		public void AppendAllText(string path, string contents) => _inner.AppendAllText(path, contents);
		public void WriteAllTextAtomic(string path, string contents) => _inner.WriteAllTextAtomic(path, contents);
		public void DeleteFile(string path) => _inner.DeleteFile(path);
	}
}
