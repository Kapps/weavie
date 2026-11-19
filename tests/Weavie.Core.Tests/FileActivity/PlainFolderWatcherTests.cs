using System.Collections.Concurrent;
using Weavie.Core.FileActivity;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Observing a plain folder that holds repositories: opening it changes nothing, and each repository beneath it
/// keeps answering for its own files as they change.
/// </summary>
public sealed class PlainFolderWatcherTests : IAsyncDisposable {
	private readonly TempDirectory _dir = new("weavie-plain-watch");
	private readonly ConcurrentBag<FileInvalidation> _changes = [];
	private readonly WorkspaceInventory _inventory;
	private readonly WorkspaceInvalidationWatcher _watcher;
	private Task _run = Task.CompletedTask;

	public PlainFolderWatcherTests() {
		_inventory = new WorkspaceInventory(_dir.Path);
		_watcher = new WorkspaceInvalidationWatcher(
			_inventory,
			batch => {
				foreach (var change in batch) _changes.Add(change);
			},
			_ => { },
			debounceMs: 20,
			Task.Delay,
			path => new FileSystemWatcher(path));
	}

	private async Task StartAsync() {
		_run = _watcher.RunAsync(CancellationToken.None);
		await _watcher.Ready;
	}

	private string InitRepository() {
		string repository = _dir.CreateDirectory("repo");
		TempGitRepo.Init(repository);
		_dir.WriteFile("repo/.gitignore", "build/\n");
		_dir.WriteFile("repo/src/a.py", "");
		return repository;
	}

	private bool Lists(params string[] segments) => _inventory.LastSnapshot!.Files.Contains(_dir.Combine(segments));

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
		using var initialized = new TempGitRepo();
		await StartAsync();
		Assert.True(Lists("project", "build", "out.o"));

		// Arrives whole: a `git init` in place is briefly a `.git` that git does not yet accept.
		Directory.Move(Path.Combine(initialized.Path, ".git"), _dir.Combine("project", ".git"));

		Assert.True(await WaitForAsync(() => !Lists("project", "build", "out.o")));
		Assert.True(Lists("project", ".gitignore"));
	}

	public async ValueTask DisposeAsync() {
		await _watcher.StopAsync();
		await _run;
		_dir.Dispose();
	}
}
