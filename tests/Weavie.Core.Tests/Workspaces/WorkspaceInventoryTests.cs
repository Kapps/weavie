using Weavie.Core.FileSystem;
using Weavie.Core.Git;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class WorkspaceInventoryTests : IDisposable {
	private readonly TempDirectory _root = new("weavie-inventory");

	[Fact]
	public void TrackNonRepositoryFile_ReportsOnlyAPathTheInventoryLacked() {
		// The workspace watcher re-enumerates whenever the inventory reports a change, so re-reporting a path
		// it already holds would re-derive the whole workspace on every write.
		var inventory = new WorkspaceInventory(_root.Path, _ => Task.FromResult<IReadOnlyList<string>?>(null));
		int reported = 0;
		inventory.Changed += () => Interlocked.Increment(ref reported);
		string path = _root.Combine("notes.md");

		inventory.TrackNonRepositoryFile(path);
		inventory.TrackNonRepositoryFile(path);
		inventory.TrackNonRepositoryFile(path);

		Assert.Equal(1, reported);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Refresh_DerivesOnlyParentsOfGitFiles(bool trailingSeparator) {
		var inventory = new WorkspaceInventory(
			trailingSeparator ? _root.Path + Path.DirectorySeparatorChar : _root.Path,
			_ => Task.FromResult<IReadOnlyList<string>?>([
				Path.Combine("src", "feature", "file.ts"),
				"README.md",
			]));

		var snapshot = await inventory.RefreshAsync();

		Assert.True(snapshot.IsRepository);
		Assert.Equal(
			new[] {
				_root.Path,
				_root.Combine("src"),
				_root.Combine("src", "feature"),
			}.Order(),
			snapshot.Directories.Order());
		Assert.Equal(2, snapshot.Files.Count);
	}

	[Fact]
	public void BuildSnapshot_DeduplicatesDirectorySpellings() {
		var inventory = new WorkspaceInventory(_root.Path);
		var snapshot = inventory.BuildSnapshot(false, [], ["src", "src/", "src/.", "./"]);
		Assert.Equal(new[] { _root.Path, _root.Combine("src") }.Order(), snapshot.Directories.Order());
	}

	[Fact]
	public async Task Refresh_DistinguishesNonRepository() {
		var inventory = new WorkspaceInventory(
			_root.Path,
			_ => Task.FromResult<IReadOnlyList<string>?>(null));

		var snapshot = await inventory.RefreshAsync();

		Assert.False(snapshot.IsRepository);
		Assert.Empty(snapshot.Files);
		Assert.Equal([_root.Path], snapshot.Directories);
	}

	[Fact]
	public async Task Refresh_RejectsPathOutsideWorkspace() {
		var inventory = new WorkspaceInventory(
			_root.Path,
			_ => Task.FromResult<IReadOnlyList<string>?>([Path.Combine("..", "outside.ts")]));

		await Assert.ThrowsAsync<GitException>(() => inventory.RefreshAsync());
	}

	[Fact]
	public async Task NonRepositoryMoveAndDeleteReconcileWholeTree() {
		var inventory = new WorkspaceInventory(
			_root.Path,
			_ => Task.FromResult<IReadOnlyList<string>?>(null));
		await inventory.RefreshAsync();
		string oldFile = _root.Combine("old", "nested", "file.ts");
		string newFile = _root.Combine("new", "nested", "file.ts");
		var seed = await inventory.BeginNonRepositorySeedAsync();
		inventory.CompleteNonRepositorySeed(seed, [oldFile], []);

		var moved = inventory.MoveNonRepositoryTree(
			_root.Combine("old"),
			_root.Combine("new"));

		Assert.Equal([new WorkspaceFileMove(oldFile, newFile)], moved);
		var afterMove = await inventory.RefreshAsync();
		Assert.Contains(newFile, afterMove.Files);
		Assert.DoesNotContain(oldFile, afterMove.Files);

		Assert.Equal([newFile], inventory.ForgetNonRepositoryTree(_root.Combine("new")));
		Assert.Empty((await inventory.RefreshAsync()).Files);
	}

	[Fact]
	public async Task NavigationSeedPreservesConcurrentWatcherFile() {
		var inventory = new WorkspaceInventory(
			_root.Path,
			_ => Task.FromResult<IReadOnlyList<string>?>(null));
		await inventory.RefreshAsync();
		var seed = await inventory.BeginNonRepositorySeedAsync();
		string createdDuringWalk = _root.Combine("during.ts");
		string navigationSeed = _root.Combine("before.ts");
		inventory.TrackNonRepositoryFile(createdDuringWalk);

		inventory.CompleteNonRepositorySeed(seed, [navigationSeed], []);

		var snapshot = await inventory.RefreshAsync();
		Assert.Contains(createdDuringWalk, snapshot.Files);
		Assert.Contains(navigationSeed, snapshot.Files);
	}

	[Fact]
	public async Task NavigationSeedPreservesConcurrentWatcherDeletion() {
		var inventory = new WorkspaceInventory(
			_root.Path,
			_ => Task.FromResult<IReadOnlyList<string>?>(null));
		await inventory.RefreshAsync();
		string deletedDuringWalk = _root.Combine("deleted.ts");
		var seed = await inventory.BeginNonRepositorySeedAsync();

		inventory.ForgetNonRepositoryFile(deletedDuringWalk);
		inventory.CompleteNonRepositorySeed(seed, [deletedDuringWalk], []);

		Assert.DoesNotContain(deletedDuringWalk, (await inventory.RefreshAsync()).Files);
	}

	[Fact]
	public async Task FileIndexSnapshotPublishedAfterWatcherMutationReplay() {
		var inventory = new WorkspaceInventory(
			_root.Path,
			_ => Task.FromResult<IReadOnlyList<string>?>(null));
		await inventory.RefreshAsync();
		string deletedDuringWalk = _root.Combine("deleted.ts");
		string createdDuringWalk = _root.Combine("created.ts");
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText(deletedDuringWalk, "");
		var fileIndex = new WorkspaceFileIndex(fileSystem, _root.Path);
		var seed = await inventory.BeginNonRepositorySeedAsync();
		var navigation = fileIndex.ListSnapshot();

		inventory.ForgetNonRepositoryFile(deletedDuringWalk);
		inventory.TrackNonRepositoryFile(createdDuringWalk);
		var completed = inventory.CompleteNonRepositorySeed(seed, navigation.Files, navigation.Directories);

		Assert.Equal([createdDuringWalk], completed.Files);
	}

	public void Dispose() => _root.Dispose();
}
