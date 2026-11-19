using Weavie.Core.FileSystem;
using Weavie.Core.Git;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class WorkspaceInventoryTests : IDisposable {
	private readonly TempDirectory _root = new("weavie-inventory");

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Refresh_DerivesOnlyParentsOfGitFiles(bool trailingSeparator) {
		var inventory = new WorkspaceInventory(
			trailingSeparator ? _root.Path + Path.DirectorySeparatorChar : _root.Path,
			_ => Task.FromResult<IReadOnlyList<string>>([
				Path.Combine("src", "feature", "file.ts"),
				"README.md",
			]));

		var snapshot = (await inventory.RefreshAsync()).Snapshot;

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
	public async Task GitInventoryIncludesNestedEmptyDirectoriesAndHonorsExcludes() {
		TempGitRepo.Init(_root.Path);
		_root.WriteFile(".gitignore", "ignored/\n");
		_root.WriteFile("tracked/deep/source.cs", "");
		TempGitRepo.Run(_root.Path, "add", ".");
		_root.WriteFile("mixed/source.cs", "");
		string empty = _root.CreateDirectory("mixed", "empty", "nested");
		string deep = _root.CreateDirectory("tracked", "deep", "empty", "nested");
		string special = _root.CreateDirectory("[literal] space", "empty");
		string ignored = _root.CreateDirectory("ignored", "empty");
		var snapshot = (await new WorkspaceInventory(_root.Path).RefreshAsync()).Snapshot;

		Assert.Contains(empty, snapshot.Directories);
		Assert.Contains(deep, snapshot.Directories);
		Assert.Contains(special, snapshot.Directories);
		Assert.DoesNotContain(ignored, snapshot.Directories);
		Assert.DoesNotContain(_root.Combine(".git"), snapshot.Directories);
		Assert.Equal(3, snapshot.Files.Count);
	}

	[Fact]
	public async Task ScopedGitRefreshMatchesFullRefresh() {
		TempGitRepo.Init(_root.Path);
		_root.WriteFile(".gitignore", "node_modules/\nbuild\n");
		_root.WriteFile("tracked/gone.cs", "");
		_root.WriteFile("tracked/kept.cs", "");
		_root.WriteFile("build/keep", "");
		TempGitRepo.Run(_root.Path, "add", ".");
		TempGitRepo.Run(_root.Path, "add", "--force", "build/keep");
		var inventory = new WorkspaceInventory(_root.Path);
		await inventory.RefreshAsync();

		Directory.Delete(_root.Combine("tracked"), recursive: true);
		Directory.Delete(_root.Combine("build"), recursive: true);
		_root.WriteFile("fresh/[x] file.cs", "");
		string empty = _root.CreateDirectory("fresh", "empty", "nested");
		_root.WriteFile("node_modules/pkg/index.js", "");
		var scoped = (await inventory.RefreshAsync(
			[_root.Combine("tracked"), _root.Combine("build"), _root.Combine("fresh"), _root.Combine("node_modules")])).Snapshot;
		var full = (await new WorkspaceInventory(_root.Path).RefreshAsync()).Snapshot;

		Assert.Equal(full.Files, scoped.Files);
		Assert.Equal(full.Directories, scoped.Directories);
		Assert.Contains(empty, scoped.Directories);
		Assert.Contains(_root.Combine("build", "keep"), scoped.Files);
	}

	[Fact]
	public async Task CoveringScopeLiftsEachPathToItsHighestUnknownAncestor() {
		var inventory = new WorkspaceInventory(_root.Path, _ => Task.FromResult<IReadOnlyList<string>>(["src/a.cs"]));
		await inventory.RefreshAsync();

		var scope = inventory.CoveringScope([
			_root.Combine("src", "b.cs"),
			_root.Combine("node_modules", "pkg", "index.js"),
			_root.Combine("node_modules", "other", "index.js"),
		]);

		Assert.Equal(new[] { _root.Combine("node_modules"), _root.Combine("src", "b.cs") }.Order(), scope.Order());
	}

	[Fact]
	public async Task CoveringScopeOfTheRootIsTheRoot() {
		var inventory = new WorkspaceInventory(_root.Path, _ => Task.FromResult<IReadOnlyList<string>>(["src/a.cs"]));
		await inventory.RefreshAsync();

		Assert.Equal([_root.Path], inventory.CoveringScope([_root.Path]));
	}

	[Fact]
	public async Task ScopedRefreshOfIgnoredPathsKeepsTheSnapshotWithoutListing() {
		TempGitRepo.Init(_root.Path);
		_root.WriteFile(".gitignore", "node_modules/\n");
		_root.WriteFile("src/a.cs", "");
		int listings = 0;
		var git = new GitService();
		var inventory = new WorkspaceInventory(
			_root.Path,
			async ct => (await git.ListWorkspacePathsAsync(_root.Path, ct))!,
			(known, discovered, ct) => {
				Interlocked.Increment(ref listings);
				return git.ListWorkspacePathsAsync(_root.Path, known, discovered, ct);
			});
		var before = (await inventory.RefreshAsync()).Snapshot;

		_root.WriteFile("node_modules/pkg/index.js", "");
		var after = (await inventory.RefreshAsync([_root.Combine("node_modules"), _root.Combine("node_modules", "pkg", "index.js")])).Snapshot;

		Assert.Same(before, after);
		Assert.Equal(1, listings);
		Assert.Empty((await git.ListWorkspacePathsAsync(_root.Path, [], [_root.Combine("node_modules", "pkg")]))!);
	}

	[Fact]
	public void Canonicalize_DeduplicatesDirectorySpellings() {
		var inventory = new WorkspaceInventory(_root.Path);
		var (_, directories) = inventory.Canonicalize([], ["src", "src/", "src/.", "./"]);
		Assert.Equal(new[] { _root.Path, _root.Combine("src") }.Order(), directories.Order());
	}

	[Fact]
	public async Task Refresh_RejectsPathOutsideWorkspace() {
		var inventory = new WorkspaceInventory(
			_root.Path,
			_ => Task.FromResult<IReadOnlyList<string>>([Path.Combine("..", "outside.ts")]));

		await Assert.ThrowsAsync<GitException>(() => inventory.RefreshAsync());
	}

	private string[] Paths(params string[] relative) => [.. relative.Select(path => _root.Combine(path.Split('/'))).Order(StringComparer.Ordinal)];

	private string InitRepository(string name) {
		string repository = _root.CreateDirectory(name);
		TempGitRepo.Init(repository);
		return repository;
	}

	[Fact]
	public async Task PlainFolderListsEachRepositoryBeneathItByItsOwnIgnoreRules() {
		_root.WriteFile("notes.md", "");
		_root.WriteFile("plain/a.txt", "");
		_root.WriteFile("plain/node_modules/pkg/index.js", "");
		InitRepository("repo");
		_root.WriteFile("repo/.gitignore", ".venv/\n");
		_root.WriteFile("repo/src/a.py", "");
		_root.WriteFile("repo/.venv/lib/site.py", "");
		_root.CreateDirectory("repo", "empty");

		var snapshot = (await new WorkspaceInventory(_root.Path).RefreshAsync()).Snapshot;

		Assert.Equal(Paths("notes.md", "plain/a.txt", "repo/.gitignore", "repo/src/a.py"), snapshot.Files.Order(StringComparer.Ordinal));
		Assert.Equal(
			Paths("plain", "repo", "repo/empty", "repo/src").Append(_root.Path).Order(StringComparer.Ordinal),
			snapshot.Directories.Order(StringComparer.Ordinal));
	}

	[Fact]
	public async Task PlainFolderWalksADirectoryWhoseGitEntryIsNotARepository() {
		_root.CreateDirectory("odd", ".git");
		_root.WriteFile("odd/file.txt", "");

		var snapshot = (await new WorkspaceInventory(_root.Path).RefreshAsync()).Snapshot;

		Assert.Equal(Paths("odd/file.txt"), snapshot.Files);
		Assert.Contains(_root.Combine("odd", ".git"), snapshot.Directories);
	}

	[Fact]
	public async Task PlainFolderListsAGitFileThatPointsNowhereAsAFile() {
		_root.WriteFile("stale/.git", "gitdir: /nowhere/at/all\n");
		_root.WriteFile("stale/file.txt", "");

		var snapshot = (await new WorkspaceInventory(_root.Path).RefreshAsync()).Snapshot;

		Assert.Equal(Paths("stale/.git", "stale/file.txt"), snapshot.Files);
		Assert.DoesNotContain(_root.Combine("stale", ".git"), snapshot.Directories);
	}

	[Fact]
	public async Task PlainFolderListsARepositoryWhoseGitDirectoryIsALink() {
		if (OperatingSystem.IsWindows()) return;
		using var store = new TempGitRepo();
		Directory.CreateSymbolicLink(Path.Combine(_root.CreateDirectory("child"), ".git"), Path.Combine(store.Path, ".git"));
		_root.WriteFile("child/.gitignore", "build/\n");
		_root.WriteFile("child/build/out.o", "");
		_root.WriteFile("child/src/a.c", "");
		var inventory = new WorkspaceInventory(_root.Path);

		var full = (await inventory.RefreshAsync()).Snapshot;
		_root.WriteFile("child/build/new.o", "");
		var scoped = (await inventory.RefreshAsync([_root.Combine("child", "build", "new.o"), _root.Combine("child", "src", "a.c")])).Snapshot;

		Assert.Equal(Paths("child/.gitignore", "child/src/a.c"), full.Files);
		Assert.Equal(full.Files, scoped.Files);
	}

	[Fact]
	public async Task ARepositoryGitCannotReadFailsTheListingByName() {
		string bad = InitRepository("bad");
		File.WriteAllText(Path.Combine(bad, ".git", "index"), "not an index");

		var failure = await Assert.ThrowsAsync<GitException>(() => new WorkspaceInventory(_root.Path).RefreshAsync());

		Assert.StartsWith(bad, failure.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AChangeARepositoryIgnoresKeepsThePlainFolderSnapshot() {
		InitRepository("repo");
		_root.WriteFile("repo/.gitignore", "build/\n");
		_root.WriteFile("repo/src/a.c", "");
		var inventory = new WorkspaceInventory(_root.Path);
		var before = (await inventory.RefreshAsync()).Snapshot;

		_root.WriteFile("repo/build/out.o", "");
		var after = (await inventory.RefreshAsync(inventory.CoveringScope([_root.Combine("repo", "build", "out.o")]))).Snapshot;

		Assert.Same(before, after);
	}

	[Fact]
	public async Task PlainFolderNeverFollowsADirectoryLink() {
		if (OperatingSystem.IsWindows()) return;
		_root.WriteFile("a/b/file.txt", "");
		Directory.CreateSymbolicLink(_root.Combine("a", "up"), "..");
		Directory.CreateSymbolicLink(_root.Combine("a", "b", "top"), _root.Path);
		File.CreateSymbolicLink(_root.Combine("a", "alias.txt"), _root.Combine("a", "b", "file.txt"));

		var snapshot = (await new WorkspaceInventory(_root.Path).RefreshAsync()).Snapshot;

		Assert.Equal(Paths("a/alias.txt", "a/b/file.txt"), snapshot.Files.Order(StringComparer.Ordinal));
	}

	[Fact]
	public async Task ScopedRefreshNeverListsThroughALinkToARepository() {
		if (OperatingSystem.IsWindows()) return;
		InitRepository("repo");
		_root.WriteFile("repo/src/a.py", "");
		var inventory = new WorkspaceInventory(_root.Path);
		await inventory.RefreshAsync();

		Directory.CreateSymbolicLink(_root.Combine("current"), _root.Combine("repo"));
		var scoped = (await inventory.RefreshAsync([_root.Combine("current"), _root.Combine("current", "src", "a.py")])).Snapshot;

		Assert.Equal(Paths("repo/src/a.py"), scoped.Files);
		Assert.Equal((await new WorkspaceInventory(_root.Path).RefreshAsync()).Snapshot.Directories, scoped.Directories);
	}

	[Fact]
	public async Task ScopedRefreshBeneathAGitEntryGitRejectsStillAsksTheRepositoriesBelowIt() {
		_root.CreateDirectory("odd", ".git");
		InitRepository(Path.Combine("odd", "sub"));
		_root.WriteFile("odd/sub/.gitignore", "build/\n");
		_root.WriteFile("odd/sub/src/a.c", "");
		var inventory = new WorkspaceInventory(_root.Path);
		await inventory.RefreshAsync();

		_root.WriteFile("odd/sub/build/out.o", "");
		_root.WriteFile("odd/sub/src/b.c", "");
		var scoped = (await inventory.RefreshAsync(
			[_root.Combine("odd", "sub", "build", "out.o"), _root.Combine("odd", "sub", "src", "b.c")])).Snapshot;
		var whole = (await inventory.RefreshAsync([_root.Combine("odd", "sub")])).Snapshot;

		var full = (await new WorkspaceInventory(_root.Path).RefreshAsync()).Snapshot;
		Assert.Equal(Paths("odd/sub/.gitignore", "odd/sub/src/a.c", "odd/sub/src/b.c"), full.Files);
		Assert.Equal(full.Files, scoped.Files);
		Assert.Equal(full.Files, whole.Files);
		Assert.Equal(full.Directories, whole.Directories);
	}

	[Fact]
	public async Task ScopedPlainFolderRefreshMatchesFullRefresh() {
		_root.WriteFile("plain/a.txt", "");
		_root.WriteFile("plain/gone.txt", "");
		InitRepository("repo");
		_root.WriteFile("repo/.gitignore", ".venv/\n");
		_root.WriteFile("repo/src/a.py", "");
		var inventory = new WorkspaceInventory(_root.Path);
		await inventory.RefreshAsync();

		File.Delete(_root.Combine("plain", "gone.txt"));
		_root.WriteFile("plain/new.txt", "");
		_root.WriteFile("plain/out", "");
		_root.WriteFile("fresh/deep/file.txt", "");
		_root.WriteFile("fresh/node_modules/pkg/index.js", "");
		_root.WriteFile("repo/src/new.py", "");
		_root.WriteFile("repo/.venv/lib/site.py", "");
		InitRepository("cloned");
		_root.WriteFile("cloned/main.go", "");
		if (!OperatingSystem.IsWindows()) Directory.CreateSymbolicLink(_root.Combine("plain", "up"), "..");
		var scoped = (await inventory.RefreshAsync([
			_root.Combine("plain", "gone.txt"),
			_root.Combine("plain", "new.txt"),
			_root.Combine("plain", "out"),
			_root.Combine("fresh"),
			_root.Combine("fresh", "node_modules", "pkg", "index.js"),
			_root.Combine("repo", "src", "new.py"),
			_root.Combine("repo", ".venv"),
			_root.Combine("cloned"),
			_root.Combine("plain", "up"),
		])).Snapshot;

		var full = (await new WorkspaceInventory(_root.Path).RefreshAsync()).Snapshot;
		Assert.Equal(full.Files, scoped.Files);
		Assert.Equal(full.Directories, scoped.Directories);
		Assert.Equal(
			Paths("cloned/main.go", "fresh/deep/file.txt", "plain/a.txt", "plain/new.txt", "plain/out", "repo/.gitignore", "repo/src/a.py", "repo/src/new.py"),
			scoped.Files.Order(StringComparer.Ordinal));
	}

	public void Dispose() => _root.Dispose();
}
