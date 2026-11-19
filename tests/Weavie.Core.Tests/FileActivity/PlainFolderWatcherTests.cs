using System.Collections.Concurrent;
using Weavie.Core.FileActivity;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Observing a plain folder that holds repositories: opening it changes nothing, and each repository beneath it
/// keeps answering for its own files as they change.
/// </summary>
public sealed class PlainFolderWatcherTests : IAsyncLifetime {
	private readonly TempDirectory _dir = new("weavie-plain-watch");
	private readonly ConcurrentBag<FileInvalidation> _changes = [];
	private WorkspaceInventory _inventory = null!;
	private WorkspaceInvalidationWatcher _watcher = null!;
	private Task _run = Task.CompletedTask;

	private Task StartAsync() => StartAsync(_dir.Path);

	private async Task StartAsync(string root) {
		_inventory = new WorkspaceInventory(root);
		_watcher = new WorkspaceInvalidationWatcher(
			_inventory,
			batch => {
				foreach (var change in batch) _changes.Add(change);
			},
			_ => { },
			debounceMs: 20,
			Task.Delay,
			path => new FileSystemWatcher(path));
		_run = _watcher.RunAsync(CancellationToken.None);
		await _watcher.Ready;
	}

	private void InitRepository() => InitRepository("repo");

	private string InitRepository(string name) {
		string repository = _dir.CreateDirectory(name);
		TempGitRepo.Init(repository);
		_dir.WriteFile($"{name}/.gitignore", "build/\n");
		_dir.WriteFile($"{name}/src/a.py", "");
		return repository;
	}

	private bool Lists(params string[] segments) => _inventory.LastSnapshot!.Files.Contains(_dir.Combine(segments));

	// The watcher is still observing when a file created now gets listed.
	private async Task AssertStillObservingAsync() {
		_dir.WriteFile("still-observed.md", "");
		Assert.True(await WaitForAsync(() => _run.IsCompleted || Lists("still-observed.md")));
		Assert.False(_run.IsCompleted, _run.Exception?.ToString());
	}

	private static async Task<bool> WaitForAsync(Func<bool> predicate) {
		for (int i = 0; i < 100 && !predicate(); i++) {
			await Task.Delay(50);
		}

		return predicate();
	}

	[Fact]
	public async Task OpeningReportsNoFileAsCreated() {
		InitRepository();
		_dir.WriteFile("notes.md", "");

		await StartAsync();
		_dir.WriteFile("later.md", "");

		Assert.True(await WaitForAsync(() => _changes.Any(change => change.Path == _dir.Combine("later.md"))));
		Assert.True(Lists("notes.md") && Lists("repo", "src", "a.py"));
		Assert.Equal([_dir.Combine("later.md")], _changes.Select(change => change.Path).Distinct());
	}

	[Fact]
	public async Task AFileTheRepositoryIgnoresIsNeverListed() {
		InitRepository();
		await StartAsync();

		_dir.WriteFile("repo/build/out.o", "");
		_dir.WriteFile("repo/src/new.py", "");

		Assert.True(await WaitForAsync(() => Lists("repo", "src", "new.py")));
		Assert.False(Lists("repo", "build", "out.o"));
		Assert.DoesNotContain(_dir.Combine("repo", "build"), _inventory.LastSnapshot!.Directories);
	}

	[Fact]
	public async Task AFolderThatBecomesARepositoryIsListedByItsIgnoreRules() {
		_dir.WriteFile("project/.gitignore", "build/\n");
		_dir.WriteFile("project/build/out.o", "");
		await StartAsync();
		Assert.True(Lists("project", "build", "out.o"));

		TempGitRepo.Init(_dir.Combine("project"));

		Assert.True(await WaitForAsync(() => !Lists("project", "build", "out.o")));
		Assert.True(Lists("project", ".gitignore"));
	}

	[Fact]
	public async Task DeletingRepositoriesKeepsObserving() {
		string[] repositories = [.. Enumerable.Range(0, 6).Select(index => InitRepository($"repo{index}"))];
		await StartAsync();

		foreach (string repository in repositories) {
			Directory.Delete(repository, recursive: true);
		}

		await AssertStillObservingAsync();
		Assert.Equal([_dir.Combine("still-observed.md")], _inventory.LastSnapshot!.Files);
	}

	[Fact]
	public async Task AShortLivedDirectoryKeepsObserving() {
		await StartAsync();

		for (int round = 0; round < 20; round++) {
			for (int index = 0; index < 20; index++) {
				_dir.WriteFile($"work{round}/big/d{index}/file.txt", "");
			}

			string shortLived = _dir.CreateDirectory($"work{round}", "tmp");
			await Task.Delay(round * 3);
			Directory.Delete(shortLived);
		}

		await AssertStillObservingAsync();
	}

	[Fact]
	public async Task ARootBeneathAGitDirectoryStillListsNewFiles() {
		string root = _dir.CreateDirectory(".git", "worktree");
		await StartAsync(root);

		File.WriteAllText(Path.Combine(root, "new.md"), "");

		Assert.True(await WaitForAsync(() => _inventory.LastSnapshot!.Files.Contains(Path.Combine(root, "new.md"))));
	}

	[Fact]
	public async Task AGitEntryGitDoesNotAcceptIsWatchedUntilItBecomesARepository() {
		_dir.WriteFile("project/.gitignore", "build/\n");
		_dir.WriteFile("project/build/out.o", "");
		using var initialized = new TempGitRepo();
		string git = _dir.CreateDirectory("project", ".git");
		await StartAsync();
		Assert.True(Lists("project", "build", "out.o"));

		foreach (string entry in Directory.GetFileSystemEntries(Path.Combine(initialized.Path, ".git"))) {
			Directory.Move(entry, Path.Combine(git, Path.GetFileName(entry)));
		}

		Assert.True(await WaitForAsync(() => !Lists("project", "build", "out.o")));
	}

	public Task InitializeAsync() => Task.CompletedTask;

	public async Task DisposeAsync() {
		await _watcher.StopAsync();
		await _run;
		_dir.Dispose();
	}
}
