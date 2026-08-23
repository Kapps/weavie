using System.Collections.Concurrent;
using System.Diagnostics;
using Weavie.Core.FileActivity;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Generic workspace observation: reports every inventoried file kind, installs only flat watches from the
/// authoritative inventory, and re-enumerates that inventory only for events that can change which paths it
/// contains — an editor autosave writing one tracked file must never re-derive the whole workspace.
/// </summary>
public sealed class WorkspaceInvalidationWatcherTests : IDisposable {
	private readonly string _dir = Path.Combine(Path.GetTempPath(), $"weavie-watch-{Guid.NewGuid():N}");
	private readonly ConcurrentBag<FileInvalidation> _changes = [];
	private readonly HashSet<string> _inventoryFiles = new(StringComparer.Ordinal);
	private int _loads;

	public WorkspaceInvalidationWatcherTests() {
		Directory.CreateDirectory(_dir);
	}

	private async Task<WatcherLease> NewWatcherAsync() {
		var inventory = new WorkspaceInventory(
			_dir,
			_ => {
				Interlocked.Increment(ref _loads);
				return Task.FromResult<IReadOnlyList<string>?>([.. _inventoryFiles]);
			});
		var watcher = new WorkspaceInvalidationWatcher(
			inventory,
			batch => {
				foreach (var change in batch) {
					_changes.Add(change);
				}
			},
			_ => { },
			debounceMs: 80,
			Task.Delay,
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
		string path = Path.Combine(_dir, fileName);
		return _changes.Any(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task ReportsChange() {
		Track("a.ts");
		await using var watcher = await NewWatcherAsync();
		await File.WriteAllTextAsync(Path.Combine(_dir, "a.ts"), "export const x = 1;\n");
		Assert.True(await WaitForAsync(() => HasChange("a.ts")), "expected a change for a.ts");
	}

	[Fact]
	public async Task ContentChangeOnATrackedFileDoesNotReEnumerateTheWorkspace() {
		string path = Path.Combine(_dir, "a.ts");
		Track("a.ts");
		await File.WriteAllTextAsync(path, "export const x = 0;\n");
		await using var watcher = await NewWatcherAsync();
		int afterStart = Volatile.Read(ref _loads);

		// What an editor autosave does, at the rate typing produces one.
		for (int i = 1; i <= 5; i++) {
			await File.WriteAllTextAsync(path, $"export const x = {i};\n");
			await Task.Delay(120);
		}

		Assert.True(await WaitForAsync(() => HasChange("a.ts")), "expected a change for a.ts");
		Assert.Equal(afterStart, Volatile.Read(ref _loads));
	}

	[Fact]
	public async Task IgnoreRuleChangeReEnumeratesTheWorkspace() {
		string path = Path.Combine(_dir, ".gitignore");
		Track(".gitignore");
		await File.WriteAllTextAsync(path, "dist/\n");
		await using var watcher = await NewWatcherAsync();
		int afterStart = Volatile.Read(ref _loads);

		await File.WriteAllTextAsync(path, "dist/\nbuild/\n");

		Assert.True(
			await WaitForAsync(() => Volatile.Read(ref _loads) > afterStart),
			"expected an edited ignore rule to re-enumerate the workspace");
	}

	[Fact]
	public async Task FileCreationReEnumeratesTheWorkspace() {
		Track("a.ts");
		await using var watcher = await NewWatcherAsync();
		int afterStart = Volatile.Read(ref _loads);

		Track("created.ts");
		await File.WriteAllTextAsync(Path.Combine(_dir, "created.ts"), "export {};\n");

		Assert.True(await WaitForAsync(() => HasChange("created.ts")), "expected a change for created.ts");
		Assert.True(
			Volatile.Read(ref _loads) > afterStart,
			"expected a created path to re-enumerate the workspace");
	}

	[Fact]
	public async Task ConsecutiveReEnumerationsWaitOutThePreviousPass() {
		var cooldowns = new ConcurrentQueue<TimeSpan>();
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int loads = 0;
		var inventory = new WorkspaceInventory(
			_dir,
			async _ => {
				if (Interlocked.Increment(ref loads) == 2) {
					await release.Task;
				}

				return (IReadOnlyList<string>?)[.. _inventoryFiles];
			});
		var watcher = new WorkspaceInvalidationWatcher(
			inventory,
			_ => { },
			_ => { },
			debounceMs: 20,
			(cooldown, _) => {
				cooldowns.Enqueue(cooldown);
				return Task.CompletedTask;
			},
			path => new FileSystemWatcher(path));
		var run = watcher.RunAsync(CancellationToken.None);
		await watcher.Ready;

		// A creation opens a second pass, held open long enough that only the pass duration — never the
		// debounce — can explain the cooldown it asks for. The hold is measured rather than assumed: it sits
		// wholly inside the pass, so the pass is at least that long however the timer rounds.
		Track("first.ts");
		await File.WriteAllTextAsync(Path.Combine(_dir, "first.ts"), "export {};\n");
		Assert.True(await WaitForAsync(() => Volatile.Read(ref loads) >= 2), "expected the creation to start a refresh");
		long heldFrom = Stopwatch.GetTimestamp();
		await Task.Delay(200);
		var held = Stopwatch.GetElapsedTime(heldFrom);
		release.SetResult();

		Assert.True(await WaitForAsync(() => !cooldowns.IsEmpty), "expected a cooldown after the refresh");
		Assert.True(cooldowns.TryPeek(out var requested));
		Assert.True(
			requested >= held,
			$"expected the cooldown to cover the {held.TotalMilliseconds}ms pass, got {requested.TotalMilliseconds}ms");

		await watcher.StopAsync();
		await run;
	}

	[Fact]
	public async Task ReportsEveryFileKind() {
		Track("notes.md");
		await using var watcher = await NewWatcherAsync();
		await File.WriteAllTextAsync(Path.Combine(_dir, "notes.md"), "hello\n");
		Assert.True(await WaitForAsync(() => HasChange("notes.md")), "expected a change for notes.md");
	}

	[Fact]
	public async Task NonRepositoryTracksNewFileFromFlatWatchEvent() {
		var inventory = new WorkspaceInventory(
			_dir,
			_ => Task.FromResult<IReadOnlyList<string>?>(null));
		var watcher = new WorkspaceInvalidationWatcher(
			inventory,
			batch => {
				foreach (var change in batch) {
					_changes.Add(change);
				}
			},
			_ => { },
			debounceMs: 20,
			Task.Delay,
			path => new FileSystemWatcher(path));
		var run = watcher.RunAsync(CancellationToken.None);
		await watcher.Ready;

		await File.WriteAllTextAsync(Path.Combine(_dir, "new.ts"), "export {};\n");
		Assert.True(await WaitForAsync(() => HasChange("new.ts")), "expected a Created change for new.ts");

		await watcher.StopAsync();
		await run;
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
		Assert.DoesNotContain(_changes, c => c.Path.Contains("node_modules", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task ReportsDeletion() {
		string path = Path.Combine(_dir, "gone.ts");
		Track("gone.ts");
		await File.WriteAllTextAsync(path, "export const g = 5;\n");
		await using var watcher = await NewWatcherAsync();
		File.Delete(path);
		Assert.True(
			await WaitForAsync(() => _changes.Any(c =>
				c.Kind == FileInvalidationKind.Deleted
				&& c.Path.EndsWith("gone.ts", StringComparison.OrdinalIgnoreCase))),
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
		using var watcher = new WorkspaceInvalidationWatcher(
			inventory,
			_ => { },
			_ => { },
			debounceMs: 1,
			Task.Delay,
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
		Assert.DoesNotContain(paths, path => path.Contains(
			$"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
			StringComparison.Ordinal));

		await watcher.StopAsync();
		await run;
	}

	[Fact]
	public async Task DirectoryCreationRefreshesInventoryAndAddsFlatWatch() {
		Track("root.ts");
		var watched = new ConcurrentBag<string>();
		var inventory = new WorkspaceInventory(
			_dir,
			_ => Task.FromResult<IReadOnlyList<string>?>([.. _inventoryFiles]));
		using var watcher = new WorkspaceInvalidationWatcher(
			inventory,
			_ => { },
			_ => { },
			debounceMs: 1,
			Task.Delay,
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

		await watcher.StopAsync();
		await run;
	}

	[Fact]
	public async Task NonRepositoryWatchesFilesCreatedInNewDirectory() {
		var watched = new ConcurrentBag<string>();
		var inventory = new WorkspaceInventory(
			_dir,
			_ => Task.FromResult<IReadOnlyList<string>?>(null));
		using var watcher = new WorkspaceInvalidationWatcher(
			inventory,
			batch => {
				foreach (var change in batch) {
					_changes.Add(change);
				}
			},
			_ => { },
			debounceMs: 20,
			Task.Delay,
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
		Assert.True(
			await WaitForAsync(() => HasChange(Path.Combine("new-directory", "new.ts"))),
			"expected the nested file change");

		await watcher.StopAsync();
		await run;
	}

	[Fact]
	public async Task NonRepositoryDoesNotTrackIgnoredDirectoryCreatedAfterStart() {
		var inventory = new WorkspaceInventory(
			_dir,
			_ => Task.FromResult<IReadOnlyList<string>?>(null));
		var watched = new ConcurrentBag<string>();
		using var watcher = new WorkspaceInvalidationWatcher(
			inventory,
			batch => {
				foreach (var change in batch) {
					_changes.Add(change);
				}
			},
			_ => { },
			debounceMs: 20,
			Task.Delay,
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

		await watcher.StopAsync();
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
		using var watcher = new WorkspaceInvalidationWatcher(
			inventory,
			batch => {
				foreach (var change in batch) {
					_changes.Add(change);
				}
			},
			_ => { },
			debounceMs: 20,
			Task.Delay,
			path => new FileSystemWatcher(path));
		var run = watcher.RunAsync(CancellationToken.None);
		await watcher.Ready;

		string renamed = Path.Combine(_dir, "renamed");
		Directory.Move(source, renamed);
		string oldPath = Path.Combine(source, "file.ts");
		string newPath = Path.Combine(renamed, "file.ts");
		Assert.True(await WaitForAsync(() =>
			_changes.Any(change => change.Path == oldPath && change.Kind == FileInvalidationKind.Deleted)
			&& _changes.Any(change => change.Path == newPath && change.Kind == FileInvalidationKind.Created)));

		await watcher.StopAsync();
		await run;
	}

	[Fact]
	public async Task StopCancelsInFlightInventoryRefresh() {
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var inventory = new WorkspaceInventory(
			_dir,
			async ct => {
				started.TrySetResult();
				try {
					await Task.Delay(Timeout.InfiniteTimeSpan, ct);
					return Array.Empty<string>();
				} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
					cancelled.TrySetResult();
					throw;
				}
			});
		var watcher = new WorkspaceInvalidationWatcher(
			inventory,
			_ => { },
			_ => { },
			debounceMs: 1,
			Task.Delay,
			path => new FileSystemWatcher(path));
		var run = watcher.RunAsync(CancellationToken.None);
		await started.Task;

		await watcher.StopAsync();
		await run;

		Assert.True(cancelled.Task.IsCompletedSuccessfully);
	}

	[Fact]
	public async Task StopFlushesPendingBatch() {
		var watcher = new WorkspaceInvalidationWatcher(
			new WorkspaceInventory(_dir, _ => Task.FromResult<IReadOnlyList<string>?>([])),
			batch => {
				foreach (var change in batch) {
					_changes.Add(change);
				}
			},
			_ => { },
			debounceMs: 30_000,
			Task.Delay,
			path => new FileSystemWatcher(path));
		watcher.Record(Path.Combine(_dir, "pending.md"), FileInvalidationKind.Changed);

		await watcher.StopAsync();

		Assert.True(HasChange("pending.md"));
	}

	[Fact]
	public async Task StopWaitsForInFlightDelivery() {
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var watcher = new WorkspaceInvalidationWatcher(
			new WorkspaceInventory(_dir, _ => Task.FromResult<IReadOnlyList<string>?>([])),
			_ => {
				entered.TrySetResult();
				release.Task.GetAwaiter().GetResult();
			},
			_ => { },
			debounceMs: 1,
			Task.Delay,
			path => new FileSystemWatcher(path));
		var run = watcher.RunAsync(CancellationToken.None);
		await watcher.Ready;
		watcher.Record(Path.Combine(_dir, "pending.md"), FileInvalidationKind.Changed);
		await entered.Task;

		var stop = Task.Run(watcher.StopAsync);
		Assert.False(stop.IsCompleted);
		release.SetResult();

		await stop;
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
			// Best-effort cleanup of operating-system watcher tests.
		}
	}

	private sealed class WatcherLease(
		WorkspaceInvalidationWatcher watcher,
		Task run) : IAsyncDisposable {
		public async ValueTask DisposeAsync() {
			await watcher.StopAsync();
			await run;
		}
	}
}
