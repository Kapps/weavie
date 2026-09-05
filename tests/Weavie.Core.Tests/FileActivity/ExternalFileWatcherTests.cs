using System.Collections.Concurrent;
using Weavie.Core.FileActivity;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// <see cref="ExternalFileWatcher"/> makes an edit to a file outside the checkout reach the open buffer. These
/// drive the real filesystem, because what they pin — that something observes the file — only exists there.
/// </summary>
public sealed class ExternalFileWatcherTests : IDisposable {
	private readonly TempDirectory _root = new("weavie-external");

	[Fact]
	public async Task ReportsAnEditMadeOutsideWeavie() {
		string watched = _root.Combine("notes.md");
		await File.WriteAllTextAsync(watched, "before\n");
		CapturingSink sink = new();
		using var watcher = NewWatcher(sink);

		watcher.Watch([watched]);
		await File.WriteAllTextAsync(watched, "after\n");

		object fact = await sink.Next.Task.WaitAsync(TimeSpan.FromSeconds(10));
		var changed = Assert.IsType<Changed>(fact);
		Assert.Equal(watched, changed.Path);
	}

	[Fact]
	public async Task ReportsADeletion() {
		string watched = _root.Combine("doomed.md");
		await File.WriteAllTextAsync(watched, "x\n");
		CapturingSink sink = new();
		using var watcher = NewWatcher(sink);

		watcher.Watch([watched]);
		File.Delete(watched);

		object fact = await sink.Next.Task.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.Equal(watched, Assert.IsType<Deleted>(fact).Path);
	}

	[Fact]
	public async Task IgnoresASiblingItWasNotAskedToWatch() {
		string watched = _root.Combine("watched.md");
		string sibling = _root.Combine("sibling.md");
		await File.WriteAllTextAsync(watched, "x\n");
		CapturingSink sink = new();
		using var watcher = NewWatcher(sink);

		// The watch is per-directory, so the sibling's events arrive and must be filtered out by path.
		watcher.Watch([watched]);
		await File.WriteAllTextAsync(sibling, "noise\n");
		await File.WriteAllTextAsync(watched, "after\n");

		object fact = await sink.Next.Task.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.Equal(watched, Assert.IsType<Changed>(fact).Path);
		Assert.DoesNotContain(sibling, sink.Paths);
	}

	[Fact]
	public async Task StopsReportingAFileThatIsNoLongerOpen() {
		string closed = _root.Combine("closed.md");
		string open = _root.Combine("open.md");
		await File.WriteAllTextAsync(closed, "x\n");
		await File.WriteAllTextAsync(open, "x\n");
		CapturingSink sink = new();
		using var watcher = NewWatcher(sink);

		watcher.Watch([closed, open]);
		watcher.Watch([open]);
		await File.WriteAllTextAsync(closed, "edited after closing\n");
		await File.WriteAllTextAsync(open, "edited while open\n");

		// The still-open file arriving proves the watch is live, so the closed one's absence is a real drop
		// rather than the assertion running before any event landed.
		object fact = await sink.Next.Task.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.Equal(open, Assert.IsType<Changed>(fact).Path);
		Assert.DoesNotContain(closed, sink.Paths);
	}

	[Fact]
	public void WatchesNothingUntilAFileOutsideTheCheckoutIsOpen() {
		// Every session reconciles an empty set when it loads and most never open an outside file. Reconcile is
		// what allocates the platform handle — on Linux an inotify instance, capped per user across every
		// session on the machine — so the assertion is that it is never reached, not that no watch resulted.
		RecordingWatchSet watchSet = new();
		using var watcher = new ExternalFileWatcher(
			new LocalFileSystem(),
			new CapturingSink(),
			Assert.Fail,
			debounceMs: 10,
			_ => watchSet);

		watcher.Watch([]);

		Assert.Equal(0, watchSet.Reconciles);
	}

	private sealed class RecordingWatchSet : IWorkspaceDirectoryWatchSet {
		public int Reconciles { get; private set; }

		public int Count => 0;

		public bool Reconcile(IReadOnlyList<string> directories) {
			Reconciles++;
			return true;
		}

		public void EnsureWatching(string directory) { }

		public void Dispose() { }
	}

	[Fact]
	public void ReleasesWatchesAsFilesLeaveTheTabSet() {
		string first = Path.Combine(_root.CreateDirectory("a"), "one.md");
		string second = Path.Combine(_root.CreateDirectory("b"), "two.md");
		using var watcher = NewWatcher(new CapturingSink());

		watcher.Watch([first, second]);
		Assert.Equal(2, watcher.WatchedDirectoryCount);

		watcher.Watch([second]);
		Assert.Equal(1, watcher.WatchedDirectoryCount);

		watcher.Watch([]);
		Assert.Equal(0, watcher.WatchedDirectoryCount);
	}

	public void Dispose() => _root.Dispose();

	private static ExternalFileWatcher NewWatcher(CapturingSink sink) =>
		new(new LocalFileSystem(), sink, failure => Assert.Fail(failure), debounceMs: 10);

	private sealed record Changed(string Path);

	private sealed record Deleted(string Path);

	private sealed class CapturingSink : IFileActivitySink {
		public TaskCompletionSource<object> Next { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public ConcurrentBag<string> Paths { get; } = [];

		public FileActivityTicket ReportBufferSaved(string path, FileStat revision) => Completed();

		public FileActivityTicket ReportChanged(string path, FileStat revision) {
			Paths.Add(path);
			Next.TrySetResult(new Changed(path));
			return Completed();
		}

		public FileActivityTicket ReportDeleted(string path) {
			Paths.Add(path);
			Next.TrySetResult(new Deleted(path));
			return Completed();
		}

		private static FileActivityTicket Completed() =>
			NoopFileActivitySink.Instance.ReportDeleted("/noop");
	}
}
