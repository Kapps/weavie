using System.Collections.Concurrent;
using Weavie.Core.Lsp;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Verifies the workspace watcher detects on-disk changes (the agentic-editor path that feeds
/// <c>workspace/didChangeWatchedFiles</c>), filters to served extensions, skips noise directories,
/// and reports deletions. Uses the real filesystem with generous polling (FileSystemWatcher is
/// inherently asynchronous), so timing is tolerant rather than exact.
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
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".ts", ".cs" },
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

	private bool HasChange(string fileName) {
		string uri = new Uri(Path.Combine(_dir, fileName)).AbsoluteUri;
		return _changes.Any(c => string.Equals(c.Uri, uri, StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task ReportsChange_ForWatchedExtension() {
		using var watcher = NewWatcher();
		await File.WriteAllTextAsync(Path.Combine(_dir, "a.ts"), "export const x = 1;\n");
		Assert.True(await WaitForAsync(() => HasChange("a.ts")), "expected a change for a.ts");
	}

	[Fact]
	public async Task IgnoresUnwatchedExtension() {
		using var watcher = NewWatcher();
		await File.WriteAllTextAsync(Path.Combine(_dir, "notes.md"), "hello\n");
		await File.WriteAllTextAsync(Path.Combine(_dir, "trigger.ts"), "export const y = 2;\n");
		await WaitForAsync(() => HasChange("trigger.ts"));
		Assert.False(HasChange("notes.md"), "markdown should be filtered out");
	}

	[Fact]
	public async Task IgnoresNoiseDirectories() {
		string nested = Path.Combine(_dir, "node_modules", "pkg");
		Directory.CreateDirectory(nested);
		using var watcher = NewWatcher();
		await File.WriteAllTextAsync(Path.Combine(nested, "dep.ts"), "export const z = 3;\n");
		await File.WriteAllTextAsync(Path.Combine(_dir, "real.ts"), "export const r = 4;\n");
		await WaitForAsync(() => HasChange("real.ts"));
		Assert.DoesNotContain(_changes, c => c.Uri.Contains("node_modules", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task ReportsDeletion() {
		string path = Path.Combine(_dir, "gone.ts");
		await File.WriteAllTextAsync(path, "export const g = 5;\n");
		using var watcher = NewWatcher();
		await Task.Delay(120);
		File.Delete(path);
		Assert.True(
			await WaitForAsync(() => _changes.Any(c => c.Kind == FileChangeKind.Deleted && c.Uri.EndsWith("gone.ts", StringComparison.OrdinalIgnoreCase))),
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
