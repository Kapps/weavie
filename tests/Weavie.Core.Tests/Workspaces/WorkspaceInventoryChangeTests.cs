using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// A refresh reports exactly what it changed, keeps the previous snapshot when that is nothing, and costs what
/// the changed paths cost rather than what the workspace holds.
/// </summary>
public sealed class WorkspaceInventoryChangeTests : IDisposable {
	private readonly TempDirectory _root = new("weavie-inventory-change");
	private readonly HashSet<string> _listing = new(StringComparer.Ordinal);

	private WorkspaceInventory NewInventory() =>
		new(_root.Path, _ => Task.FromResult<IReadOnlyList<string>>([.. _listing]));

	private string[] Paths(params string[] relative) => [.. relative.Select(path => _root.Combine(path.Split('/')))];

	[Fact]
	public async Task TheFirstRefreshReportsEverythingAsNew() {
		_listing.UnionWith(["a.cs", "src/b.cs"]);

		var change = await NewInventory().RefreshAsync();

		Assert.Equal(Paths("a.cs", "src/b.cs"), change.CreatedFiles.Order(StringComparer.Ordinal));
		Assert.Equal([_root.Path, .. Paths("src")], change.AddedDirectories.Order(StringComparer.Ordinal));
		Assert.Empty(change.DeletedFiles);
		Assert.Empty(change.RemovedDirectories);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ARefreshReportsExactlyWhatChanged(bool scoped) {
		_listing.UnionWith(["keep.cs", "old/gone.cs", "old/deep/gone.cs", "src/stay.cs"]);
		var inventory = NewInventory();
		var before = (await inventory.RefreshAsync()).Snapshot;

		_listing.ExceptWith(["old/gone.cs", "old/deep/gone.cs"]);
		_listing.UnionWith(["src/new.cs", "fresh/deep/new.cs", "empty/"]);
		var change = scoped
			? await inventory.RefreshAsync(Paths("old", "src/new.cs", "fresh", "empty"))
			: await inventory.RefreshAsync();

		Assert.Equal(Paths("fresh/deep/new.cs", "src/new.cs"), change.CreatedFiles.Order(StringComparer.Ordinal));
		Assert.Equal(Paths("old/deep/gone.cs", "old/gone.cs"), change.DeletedFiles.Order(StringComparer.Ordinal));
		Assert.Equal(Paths("empty", "fresh", "fresh/deep"), change.AddedDirectories.Order(StringComparer.Ordinal));
		Assert.Equal(Paths("old", "old/deep"), change.RemovedDirectories.Order(StringComparer.Ordinal));
		Assert.Equal(
			before.Files.Except(change.DeletedFiles).Union(change.CreatedFiles),
			change.Snapshot.Files);
		Assert.Equal((await NewInventory().RefreshAsync()).Snapshot.Files, change.Snapshot.Files);
		Assert.Equal((await NewInventory().RefreshAsync()).Snapshot.Directories, change.Snapshot.Directories);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ARefreshThatFindsNothingNewKeepsTheSnapshot(bool scoped) {
		_listing.UnionWith(["a.cs", "src/b.cs"]);
		var inventory = NewInventory();
		var before = (await inventory.RefreshAsync()).Snapshot;

		var change = scoped ? await inventory.RefreshAsync(Paths("src")) : await inventory.RefreshAsync();

		Assert.True(change.IsEmpty);
		Assert.Same(before, change.Snapshot);
		Assert.Same(before, inventory.LastSnapshot);
	}

	[Fact]
	public async Task AScopedRefreshCostsWhatItListsNotWhatTheWorkspaceHolds() {
		string[] workspace = [.. Enumerable.Range(0, 200_000).Select(index => $"d{index % 500}/f{index}.cs")];
		var inventory = new WorkspaceInventory(
			_root.Path,
			_ => Task.FromResult<IReadOnlyList<string>>(workspace),
			(_, discovered, _) => Task.FromResult<IReadOnlyList<string>?>(
				[.. discovered.Select(path => Path.GetRelativePath(_root.Path, path))]));
		await inventory.RefreshAsync();
		string[] scope = Paths("d7/created.cs");
		await inventory.RefreshAsync(Paths("d8/warm.cs"));

		long before = GC.GetAllocatedBytesForCurrentThread();
		var refresh = inventory.RefreshAsync(scope);
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		// Completing without yielding is what makes one thread's allocation count the whole refresh.
		Assert.True(refresh.IsCompletedSuccessfully);
		var change = await refresh;

		Assert.Equal(scope, change.CreatedFiles);
		Assert.Equal(200_002, change.Snapshot.Files.Count);
		Assert.True(allocated < 64 * 1024, $"a one-file refresh over 200,000 files allocated {allocated:N0} bytes");
	}

	[Fact]
	public async Task RelativePathIsWhatFollowsTheRootEvenWhenTheRootIsAFilesystemRoot() {
		string filesystemRoot = Path.GetPathRoot(_root.Path)!;
		var inventory = new WorkspaceInventory(filesystemRoot, _ => Task.FromResult<IReadOnlyList<string>>(["src/a.ts"]));
		var nested = NewInventory();
		_listing.Add("src/a.ts");

		string atRoot = Assert.Single((await inventory.RefreshAsync()).Snapshot.Files);
		string below = Assert.Single((await nested.RefreshAsync()).Snapshot.Files);

		Assert.Equal("src/a.ts", inventory.RelativePath(atRoot));
		Assert.Equal("src/a.ts", nested.RelativePath(below));
	}

	public void Dispose() => _root.Dispose();
}
