using Weavie.Core.FileSystem;
using Weavie.Core.Git;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class WorkspaceInventoryTests : IDisposable {
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"weavie-inventory-{Guid.NewGuid():N}");

	public WorkspaceInventoryTests() {
		Directory.CreateDirectory(_root);
	}

	[Fact]
	public void TrackNonRepositoryFile_ReportsOnlyAPathTheInventoryLacked() {
		// The workspace watcher re-enumerates whenever the inventory reports a change, so re-reporting a path
		// it already holds would re-derive the whole workspace on every write.
		var inventory = new WorkspaceInventory(_root, _ => Task.FromResult<IReadOnlyList<string>?>(null));
		int reported = 0;
		inventory.Changed += () => Interlocked.Increment(ref reported);
		string path = Path.Combine(_root, "notes.md");

		inventory.TrackNonRepositoryFile(path);
		inventory.TrackNonRepositoryFile(path);
		inventory.TrackNonRepositoryFile(path);

		Assert.Equal(1, reported);
	}

	[Fact]
	public async Task Refresh_DerivesOnlyParentsOfGitFiles() {
		var inventory = new WorkspaceInventory(
			_root,
			_ => Task.FromResult<IReadOnlyList<string>?>([
				Path.Combine("src", "feature", "file.ts"),
				"README.md",
			]));

		var snapshot = await inventory.RefreshAsync();

		Assert.True(snapshot.IsRepository);
		Assert.Equal(
			new[] {
				_root,
				Path.Combine(_root, "src"),
				Path.Combine(_root, "src", "feature"),
			}.Order(),
			snapshot.Directories.Order());
		Assert.Equal(2, snapshot.Files.Count);
	}

	[Fact]
	public async Task Refresh_DistinguishesNonRepository() {
		var inventory = new WorkspaceInventory(
			_root,
			_ => Task.FromResult<IReadOnlyList<string>?>(null));

		var snapshot = await inventory.RefreshAsync();

		Assert.False(snapshot.IsRepository);
		Assert.Empty(snapshot.Files);
		Assert.Equal([_root], snapshot.Directories);
	}

	[Fact]
	public async Task Refresh_RejectsPathOutsideWorkspace() {
		var inventory = new WorkspaceInventory(
			_root,
			_ => Task.FromResult<IReadOnlyList<string>?>([Path.Combine("..", "outside.ts")]));

		await Assert.ThrowsAsync<GitException>(() => inventory.RefreshAsync());
	}

	[Fact]
	public async Task NonRepositoryMoveAndDeleteReconcileWholeTree() {
		var inventory = new WorkspaceInventory(
			_root,
			_ => Task.FromResult<IReadOnlyList<string>?>(null));
		await inventory.RefreshAsync();
		string oldFile = Path.Combine(_root, "old", "nested", "file.ts");
		string newFile = Path.Combine(_root, "new", "nested", "file.ts");
		var seed = await inventory.BeginNonRepositorySeedAsync();
		inventory.CompleteNonRepositorySeed(seed, [oldFile], []);

		var moved = inventory.MoveNonRepositoryTree(
			Path.Combine(_root, "old"),
			Path.Combine(_root, "new"));

		Assert.Equal([new WorkspaceFileMove(oldFile, newFile)], moved);
		var afterMove = await inventory.RefreshAsync();
		Assert.Contains(newFile, afterMove.Files);
		Assert.DoesNotContain(oldFile, afterMove.Files);

		Assert.Equal([newFile], inventory.ForgetNonRepositoryTree(Path.Combine(_root, "new")));
		Assert.Empty((await inventory.RefreshAsync()).Files);
	}

	[Fact]
	public async Task NavigationSeedPreservesConcurrentWatcherFile() {
		var inventory = new WorkspaceInventory(
			_root,
			_ => Task.FromResult<IReadOnlyList<string>?>(null));
		await inventory.RefreshAsync();
		var seed = await inventory.BeginNonRepositorySeedAsync();
		string createdDuringWalk = Path.Combine(_root, "during.ts");
		string navigationSeed = Path.Combine(_root, "before.ts");
		inventory.TrackNonRepositoryFile(createdDuringWalk);

		inventory.CompleteNonRepositorySeed(seed, [navigationSeed], []);

		var snapshot = await inventory.RefreshAsync();
		Assert.Contains(createdDuringWalk, snapshot.Files);
		Assert.Contains(navigationSeed, snapshot.Files);
	}

	[Fact]
	public async Task NavigationSeedPreservesConcurrentWatcherDeletion() {
		var inventory = new WorkspaceInventory(
			_root,
			_ => Task.FromResult<IReadOnlyList<string>?>(null));
		await inventory.RefreshAsync();
		string deletedDuringWalk = Path.Combine(_root, "deleted.ts");
		var seed = await inventory.BeginNonRepositorySeedAsync();

		inventory.ForgetNonRepositoryFile(deletedDuringWalk);
		inventory.CompleteNonRepositorySeed(seed, [deletedDuringWalk], []);

		Assert.DoesNotContain(deletedDuringWalk, (await inventory.RefreshAsync()).Files);
	}

	[Fact]
	public async Task FileIndexSnapshotPublishedAfterWatcherMutationReplay() {
		var inventory = new WorkspaceInventory(
			_root,
			_ => Task.FromResult<IReadOnlyList<string>?>(null));
		await inventory.RefreshAsync();
		string deletedDuringWalk = Path.Combine(_root, "deleted.ts");
		string createdDuringWalk = Path.Combine(_root, "created.ts");
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText(deletedDuringWalk, "");
		var fileIndex = new WorkspaceFileIndex(fileSystem, _root);
		var seed = await inventory.BeginNonRepositorySeedAsync();
		var navigation = fileIndex.ListSnapshot();

		inventory.ForgetNonRepositoryFile(deletedDuringWalk);
		inventory.TrackNonRepositoryFile(createdDuringWalk);
		var completed = inventory.CompleteNonRepositorySeed(seed, navigation.Files, navigation.Directories);

		Assert.Equal([createdDuringWalk], completed.Files);
	}

	public void Dispose() => Directory.Delete(_root, recursive: true);
}
