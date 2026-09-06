using Weavie.Core.FileActivity;
using Weavie.Core.Processes;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class WorkspaceDirectoryWatchSetTests : IDisposable {
	private readonly TempDirectory _root = new("weavie-flat-watch");

	[Fact]
	public void VanishedDirectoryDoesNotFailWatchRegistration() {
		using var watches = Create(path => new FileSystemWatcher(path));

		watches.Reconcile([_root.Path, _root.Combine("gone")]);
		watches.EnsureWatching(_root.Combine("also-gone"));

		Assert.Equal(1, watches.Count);
	}

	[Fact]
	public void AccessFailureIsNotClassifiedAsVanishedDirectory() {
		using var watches = Create(_ => throw new UnauthorizedAccessException("denied"));

		Assert.Throws<UnauthorizedAccessException>(() => watches.Reconcile([_root.Path]));
	}

	public void Dispose() => _root.Dispose();

	private static FileSystemWorkspaceDirectoryWatchSet Create(Func<string, FileSystemWatcher> factory) =>
		new(factory, _ => { }, _ => { }, _ => { }, (_, _) => { }, _ => { }, recursive: false);

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void RecursiveRootsReleaseDescendantHandlesBeforeAncestorRename(bool childFirst) {
		using var root = new TempDirectory("weavie-watch-roots");
		string child = root.CreateDirectory("parent", "child");
		using var watches = NewWatchSet(_ => { });
		watches.EnsureWatching(childFirst ? child : root.Path);
		watches.EnsureWatching(childFirst ? root.Path : child);
		Assert.Equal(1, watches.Count);

		Directory.Move(root.Combine("parent"), root.Combine("renamed"));
		string moved = root.Combine("renamed", "child");
		Assert.True(Directory.Exists(moved));
		Assert.True(watches.Reconcile([moved]));
		Assert.Equal(1, watches.Count);
		Assert.True(watches.Reconcile([]));
		Assert.Equal(0, watches.Count);
	}

	[Fact]
	public async Task RecursiveRootsKeepExplicitCoverageAcrossDirectoryLinks() {
		using var root = new TempDirectory("weavie-watch-root");
		using var outside = new TempDirectory("weavie-watch-target");
		string link = root.Combine("link");
		if (OperatingSystem.IsWindows()) {
			var result = await ProcessCapture.RunAsync(new ProcessCaptureRequest {
				FileName = "cmd.exe",
				Arguments = ["/c", "mklink", "/J", link, outside.Path],
			}, CancellationToken.None);
			Assert.Equal(0, result.ExitCode);
		} else {
			Directory.CreateSymbolicLink(link, outside.Path);
		}
		var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var watches = NewWatchSet(change => {
			if (change.FullPath == Path.Combine(link, "new.txt")) observed.TrySetResult();
		});
		watches.EnsureWatching(root.Path);
		watches.EnsureWatching(link);
		Assert.Equal(2, watches.Count);
		await File.WriteAllTextAsync(outside.Combine("new.txt"), "new");
		await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
	}

	private static FileSystemWorkspaceDirectoryWatchSet NewWatchSet(Action<FileSystemEventArgs> changed) =>
		new(path => new FileSystemWatcher(path), changed, changed, changed, (_, _) => { }, error => Assert.Fail(error.Message), recursive: true);
}
