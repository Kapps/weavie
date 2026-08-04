using System.Collections.Concurrent;
using System.Diagnostics;
using Weavie.Core.Lsp;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Workspace watcher (feeds <c>workspace/didChangeWatchedFiles</c>): detects on-disk changes, filters
/// to served extensions, and installs only flat watches from the authoritative inventory.
/// </summary>
public sealed class WorkspaceWatcherTests : IDisposable {
	private readonly string _dir = Path.Combine(Path.GetTempPath(), $"weavie-watch-{Guid.NewGuid():N}");
	private readonly ConcurrentBag<WatchedFileChange> _changes = [];
	private readonly HashSet<string> _inventoryFiles = new(StringComparer.Ordinal);

	public WorkspaceWatcherTests() {
		Directory.CreateDirectory(_dir);
	}

	private async Task<WatcherLease> NewWatcherAsync() {
		var inventory = new WorkspaceInventory(
			_dir,
			_ => Task.FromResult<IReadOnlyList<string>?>([.. _inventoryFiles]));
		var watcher = new WorkspaceWatcher(
			inventory,
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".ts", ".cs" },
			batch => {
				foreach (var change in batch) {
					_changes.Add(change);
				}
			},
			_ => { },
			debounceMs: 80,
			TimeSpan.FromHours(1),
			path => new FileSystemWatcher(path));
		var run = watcher.RunAsync(CancellationToken.None);
		await watcher.Ready;
		return new WatcherLease(watcher, run);
	}

	private void Track(string relativePath) => _inventoryFiles.Add(relativePath);

	private async Task<bool> WaitForAsync(Func<bool> predicate) {
		for (int i = 0; i < 100; i++) {
			if (predicate()) {
				return true;
			}

			await Task.Delay(50);
		}

		return predicate();
	}

	private bool HasChange(string fileName) {
		string uri = new Uri(Path.Combine(_dir, fileName)).AbsoluteUri;
		return _changes.Any(c => string.Equals(c.Uri, uri, StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task ReportsChange_ForWatchedExtension() {
		Track("a.ts");
		await using var watcher = await NewWatcherAsync();
		await File.WriteAllTextAsync(Path.Combine(_dir, "a.ts"), "export const x = 1;\n");
		Assert.True(await WaitForAsync(() => HasChange("a.ts")), "expected a change for a.ts");
	}

	[Fact]
	public async Task NonRepositoryTracksNewFileFromFlatWatchEvent() {
		var inventory = new WorkspaceInventory(
			_dir,
			_ => Task.FromResult<IReadOnlyList<string>?>(null));
		var watcher = new WorkspaceWatcher(
			inventory,
			new HashSet<string> { ".ts" },
			batch => {
				foreach (var change in batch) {
					_changes.Add(change);
				}
			},
			_ => { },
			debounceMs: 20,
			TimeSpan.FromHours(1),
			path => new FileSystemWatcher(path));
		var run = watcher.RunAsync(CancellationToken.None);
		await watcher.Ready;

		await File.WriteAllTextAsync(Path.Combine(_dir, "new.ts"), "export {};\n");
		Assert.True(await WaitForAsync(() => HasChange("new.ts")), "expected a Created change for new.ts");

		watcher.Dispose();
		await run;
	}

	[Fact]
	public async Task IgnoresUnwatchedExtension() {
		Track("notes.md");
		Track("trigger.ts");
		await using var watcher = await NewWatcherAsync();
		await File.WriteAllTextAsync(Path.Combine(_dir, "notes.md"), "hello\n");
		await File.WriteAllTextAsync(Path.Combine(_dir, "trigger.ts"), "export const y = 2;\n");
		await WaitForAsync(() => HasChange("trigger.ts"));
		Assert.False(HasChange("notes.md"), "markdown should be filtered out");
	}

	[Fact]
	public async Task DoesNotWatchDirectoriesAbsentFromInventory() {
		string nested = Path.Combine(_dir, "node_modules", "pkg");
		Directory.CreateDirectory(nested);
		Track("real.ts");
		await using var watcher = await NewWatcherAsync();
		await File.WriteAllTextAsync(Path.Combine(nested, "dep.ts"), "export const z = 3;\n");
		await File.WriteAllTextAsync(Path.Combine(_dir, "real.ts"), "export const r = 4;\n");
		await WaitForAsync(() => HasChange("real.ts"));
		Assert.DoesNotContain(_changes, c => c.Uri.Contains("node_modules", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task ReportsDeletion() {
		string path = Path.Combine(_dir, "gone.ts");
		Track("gone.ts");
		await File.WriteAllTextAsync(path, "export const g = 5;\n");
		await using var watcher = await NewWatcherAsync();
		File.Delete(path);
		Assert.True(
			await WaitForAsync(() => _changes.Any(c => c.Kind == FileChangeKind.Deleted && c.Uri.EndsWith("gone.ts", StringComparison.OrdinalIgnoreCase))),
			"expected a Deleted change for gone.ts");
	}

	[Fact]
	public async Task InstallsOnlyFlatInventoryDirectories() {
		string source = Path.Combine(_dir, "src", "feature");
		Directory.CreateDirectory(source);
		Directory.CreateDirectory(Path.Combine(_dir, ".git", "objects", "00"));
		var paths = new ConcurrentBag<string>();
		var watchers = new ConcurrentBag<FileSystemWatcher>();
		var inventory = new WorkspaceInventory(
			_dir,
			_ => Task.FromResult<IReadOnlyList<string>?>([Path.Combine("src", "feature", "file.ts")]));
		using var watcher = new WorkspaceWatcher(
			inventory,
			new HashSet<string> { ".ts" },
			_ => { },
			_ => { },
			debounceMs: 1,
			TimeSpan.FromHours(1),
			path => {
				paths.Add(path);
				var created = new FileSystemWatcher(path);
				watchers.Add(created);
				return created;
			});
		var run = watcher.RunAsync(CancellationToken.None);
		await watcher.Ready;

		Assert.Equal(
			new[] { _dir, Path.Combine(_dir, "src"), source }.Order(),
			paths.Order());
		Assert.All(watchers, created => Assert.False(created.IncludeSubdirectories));
		Assert.DoesNotContain(paths, path => path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

		watcher.Dispose();
		await run;
	}

	[Fact]
	public async Task DirectoryCreationRefreshesInventoryAndAddsFlatWatch() {
		Track("root.ts");
		var watched = new ConcurrentBag<string>();
		var inventory = new WorkspaceInventory(
			_dir,
			_ => Task.FromResult<IReadOnlyList<string>?>([.. _inventoryFiles]));
		using var watcher = new WorkspaceWatcher(
			inventory,
			new HashSet<string> { ".ts" },
			_ => { },
			_ => { },
			debounceMs: 1,
			TimeSpan.FromHours(1),
			path => {
				watched.Add(path);
				return new FileSystemWatcher(path);
			});
		var run = watcher.RunAsync(CancellationToken.None);
		await watcher.Ready;

		Track(Path.Combine("src", "file.ts"));
		string source = Path.Combine(_dir, "src");
		Directory.CreateDirectory(source);
		Assert.True(await WaitForAsync(() => watched.Contains(source)), "expected a flat watch for the new directory");

		watcher.Dispose();
		await run;
	}

	[Fact]
	public async Task NonRepositoryWatchesFilesCreatedInNewDirectory() {
		var watched = new ConcurrentBag<string>();
		var inventory = new WorkspaceInventory(
			_dir,
			_ => Task.FromResult<IReadOnlyList<string>?>(null));
		using var watcher = new WorkspaceWatcher(
			inventory,
			new HashSet<string> { ".ts" },
			batch => {
				foreach (var change in batch) {
					_changes.Add(change);
				}
			},
			_ => { },
			debounceMs: 20,
			TimeSpan.FromHours(1),
			path => {
				watched.Add(path);
				return new FileSystemWatcher(path);
			});
		var run = watcher.RunAsync(CancellationToken.None);
		await watcher.Ready;

		string nested = Path.Combine(_dir, "new-directory");
		Directory.CreateDirectory(nested);
		Assert.True(await WaitForAsync(() => watched.Contains(nested)), "expected the new directory to be watched");
		await File.WriteAllTextAsync(Path.Combine(nested, "new.ts"), "export {};\n");
		Assert.True(await WaitForAsync(() => HasChange(Path.Combine("new-directory", "new.ts"))), "expected the nested file change");

		watcher.Dispose();
		await run;
	}

	[Fact]
	public async Task NonRepositoryDoesNotTrackIgnoredDirectoryCreatedAfterStart() {
		var inventory = new WorkspaceInventory(
			_dir,
			_ => Task.FromResult<IReadOnlyList<string>?>(null));
		var watched = new ConcurrentBag<string>();
		using var watcher = new WorkspaceWatcher(
			inventory,
			new HashSet<string> { ".ts" },
			batch => {
				foreach (var change in batch) {
					_changes.Add(change);
				}
			},
			_ => { },
			debounceMs: 20,
			TimeSpan.FromHours(1),
			path => {
				watched.Add(path);
				return new FileSystemWatcher(path);
			});
		var run = watcher.RunAsync(CancellationToken.None);
		await watcher.Ready;

		string ignored = Path.Combine(_dir, "node_modules");
		Directory.CreateDirectory(ignored);
		await Task.Delay(100);
		await File.WriteAllTextAsync(Path.Combine(ignored, "ignored.ts"), "export {};\n");
		await File.WriteAllTextAsync(Path.Combine(_dir, "visible.ts"), "export {};\n");
		Assert.True(await WaitForAsync(() => HasChange("visible.ts")));
		Assert.DoesNotContain(ignored, watched);
		Assert.False(HasChange(Path.Combine("node_modules", "ignored.ts")));

		watcher.Dispose();
		await run;
	}

	[Fact]
	public async Task GitTrackedDirectoryRenameReportsEveryDescendant() {
		RunGit(_dir, "init", "--quiet");
		string source = Path.Combine(_dir, "src");
		Directory.CreateDirectory(source);
		await File.WriteAllTextAsync(Path.Combine(source, "file.ts"), "export {};\n");
		RunGit(_dir, "add", "src/file.ts");
		var inventory = new WorkspaceInventory(_dir);
		using var watcher = new WorkspaceWatcher(
			inventory,
			new HashSet<string> { ".ts" },
			batch => {
				foreach (var change in batch) {
					_changes.Add(change);
				}
			},
			_ => { },
			debounceMs: 20,
			TimeSpan.FromHours(1),
			path => new FileSystemWatcher(path));
		var run = watcher.RunAsync(CancellationToken.None);
		await watcher.Ready;

		string renamed = Path.Combine(_dir, "renamed");
		Directory.Move(source, renamed);
		string oldUri = new Uri(Path.Combine(source, "file.ts")).AbsoluteUri;
		string newUri = new Uri(Path.Combine(renamed, "file.ts")).AbsoluteUri;
		Assert.True(await WaitForAsync(() =>
			_changes.Any(change => change.Uri == oldUri && change.Kind == FileChangeKind.Deleted)
			&& _changes.Any(change => change.Uri == newUri && change.Kind == FileChangeKind.Created)));

		watcher.Dispose();
		await run;
	}

	private static void RunGit(string workingDirectory, params string[] args) {
		var start = new ProcessStartInfo {
			FileName = "git",
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
		};
		foreach (string arg in args) {
			start.ArgumentList.Add(arg);
		}

		using var process = Process.Start(start) ?? throw new InvalidOperationException("git failed to start");
		process.WaitForExit();
		Assert.Equal(0, process.ExitCode);
	}

	public void Dispose() {
		try {
			Directory.Delete(_dir, recursive: true);
		} catch (IOException) {
			// best-effort cleanup
		}
	}

	private sealed class WatcherLease(WorkspaceWatcher watcher, Task run) : IAsyncDisposable {
		public async ValueTask DisposeAsync() {
			watcher.Dispose();
			await run;
		}
	}
}
