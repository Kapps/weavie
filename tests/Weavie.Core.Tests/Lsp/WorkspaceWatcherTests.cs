using System.Collections.Concurrent;
using Weavie.Core.Lsp;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Workspace watcher: detects on-disk file AND directory changes (every extension — consumers filter), skips
/// noise directories, and reports deletions. Uses the real filesystem with generous polling, since
/// FileSystemWatcher is asynchronous.
/// </summary>
public sealed class WorkspaceWatcherTests : IDisposable {
	private readonly string _dir = Path.Combine(Path.GetTempPath(), $"weavie-watch-{Guid.NewGuid():N}");
	private readonly ConcurrentBag<WatchedFileChange> _changes = [];

	public WorkspaceWatcherTests() {
		Directory.CreateDirectory(_dir);
	}

	private WorkspaceWatcher NewWatcher() {
		var watcher = new WorkspaceWatcher(
			_dir,
			batch => {
				foreach (var change in batch) {
					_changes.Add(change);
				}
			},
			_ => { },
			debounceMs: 80);
		watcher.Start();
		return watcher;
	}

	private async Task<bool> WaitForAsync(Func<bool> predicate) {
		for (int i = 0; i < 100; i++) {
			if (predicate()) {
				return true;
			}

			await Task.Delay(50);
		}

		return predicate();
	}

	private bool HasChange(string name) {
		string path = Path.Combine(_dir, name);
		return _changes.Any(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task ReportsChange_WithNativePath() {
		using var watcher = NewWatcher();
		await File.WriteAllTextAsync(Path.Combine(_dir, "a.ts"), "export const x = 1;\n");
		Assert.True(await WaitForAsync(() => HasChange("a.ts")), "expected a change for a.ts");
	}

	[Fact]
	public async Task NewFile_CoalescesToCreated_NotChanged() {
		// Creating a file fires Created + content-write Changed inside one debounce window; the batch must
		// keep Created — consumers gate tree/index membership on it.
		using var watcher = NewWatcher();
		await File.WriteAllTextAsync(Path.Combine(_dir, "fresh.ts"), "export const f = 1;\n");
		Assert.True(await WaitForAsync(() => HasChange("fresh.ts")), "expected a change for fresh.ts");
		Assert.Contains(
			_changes, c => c.Kind == FileChangeKind.Created && c.Path.EndsWith("fresh.ts", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task ReportsEveryExtension() {
		using var watcher = NewWatcher();
		await File.WriteAllTextAsync(Path.Combine(_dir, "notes.md"), "hello\n");
		Assert.True(await WaitForAsync(() => HasChange("notes.md")), "expected a change for notes.md");
	}

	[Fact]
	public async Task ReportsDirectoryCreation() {
		using var watcher = NewWatcher();
		Directory.CreateDirectory(Path.Combine(_dir, "newdir"));
		Assert.True(
			await WaitForAsync(() => _changes.Any(c => c.Kind == FileChangeKind.Created && HasChange("newdir"))),
			"expected a Created change for newdir");
	}

	[Fact]
	public async Task IgnoresNoiseDirectories() {
		string nested = Path.Combine(_dir, "node_modules", "pkg");
		Directory.CreateDirectory(nested);
		using var watcher = NewWatcher();
		await File.WriteAllTextAsync(Path.Combine(nested, "dep.ts"), "export const z = 3;\n");
		await File.WriteAllTextAsync(Path.Combine(_dir, "real.ts"), "export const r = 4;\n");
		await WaitForAsync(() => HasChange("real.ts"));
		Assert.DoesNotContain(_changes, c => c.Path.Contains("node_modules", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task ReportsDeletion() {
		string path = Path.Combine(_dir, "gone.ts");
		await File.WriteAllTextAsync(path, "export const g = 5;\n");
		using var watcher = NewWatcher();
		await Task.Delay(120);
		File.Delete(path);
		Assert.True(
			await WaitForAsync(() => _changes.Any(c => c.Kind == FileChangeKind.Deleted && c.Path.EndsWith("gone.ts", StringComparison.OrdinalIgnoreCase))),
			"expected a Deleted change for gone.ts");
	}

	public void Dispose() {
		try {
			Directory.Delete(_dir, recursive: true);
		} catch (IOException) {
			// best-effort cleanup
		}
	}
}
