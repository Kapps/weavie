using Weavie.Core.Changes;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Core.Tests.Changes;

public sealed class AuthoredLineTrackerTests {
	[Fact]
	public void OnRead_SeedsUnmarkedLinesAtTheCanonicalPath() {
		var tracker = new AuthoredLineTracker();
		string path = FilePath("nested", "file.txt");

		tracker.OnRead(Path.Combine(Path.GetDirectoryName(path)!, ".", "file.txt"), "one\ntwo");

		var snapshot = RequireSnapshot(tracker, path);
		Assert.Equal(WorkspacePaths.CanonicalFsPath(Path.GetFullPath(path)), snapshot.Path);
		Assert.Equal(1, snapshot.Version);
		Assert.Empty(snapshot.Lines);
	}

	[Fact]
	public void OnWrite_WithoutAPriorMirrorMarksEveryLine() {
		var tracker = new AuthoredLineTracker();
		string path = FilePath("new.txt");

		tracker.OnWrite(path, "one\ntwo\n");

		var snapshot = RequireSnapshot(tracker, path);
		Assert.Equal(1, snapshot.Version);
		Assert.Equal([
			new AuthoredLine(1, "one"),
			new AuthoredLine(2, "two"),
			new AuthoredLine(3, ""),
		], snapshot.Lines);
	}

	[Fact]
	public void OnWrite_PreservesEqualLinesAndMarksInsertedOrReplacedLines() {
		var tracker = new AuthoredLineTracker();
		string path = FilePath("file.txt");
		tracker.OnRead(path, "first\nmiddle\nlast");

		tracker.OnWrite(path, "first\nmanual\nadded\nlast");

		var snapshot = RequireSnapshot(tracker, path);
		Assert.Equal(2, snapshot.Version);
		Assert.Equal([
			new AuthoredLine(2, "manual"),
			new AuthoredLine(3, "added"),
		], snapshot.Lines);
	}

	[Fact]
	public void OnRead_PreservesEqualManualLinesAndClearsExternalChanges() {
		var tracker = new AuthoredLineTracker();
		string path = FilePath("file.txt");
		tracker.OnRead(path, "first\nmiddle\nlast");
		tracker.OnWrite(path, "first\nmanual\nlast");

		tracker.OnRead(path, "first\nexternal\nmanual\nlast");

		var snapshot = RequireSnapshot(tracker, path);
		Assert.Equal(3, snapshot.Version);
		Assert.Equal([new AuthoredLine(3, "manual")], snapshot.Lines);
	}

	[Fact]
	public void CrLfAndLf_AlignWithoutDiscardingManualAuthorship() {
		var tracker = new AuthoredLineTracker();
		string path = FilePath("file.txt");
		tracker.OnRead(path, "first\r\nmiddle\r\n");
		tracker.OnWrite(path, "first\r\nmanual\r\n");

		tracker.OnRead(path, "first\nmanual\n");
		tracker.OnWrite(path, "first\nmanual\nadded\n");

		var snapshot = RequireSnapshot(tracker, path);
		Assert.Equal([
			new AuthoredLine(2, "manual"),
			new AuthoredLine(3, "added"),
		], snapshot.Lines);
	}

	[Fact]
	public void CrLfAndLfOnlyRead_DoesNotCreateAuthorship() {
		var tracker = new AuthoredLineTracker();
		string path = FilePath("file.txt");
		tracker.OnRead(path, "first\r\nsecond\r\n");

		tracker.OnRead(path, "first\nsecond\n");

		var snapshot = RequireSnapshot(tracker, path);
		Assert.Equal(2, snapshot.Version);
		Assert.Empty(snapshot.Lines);
	}

	[Fact]
	public void Snapshot_IsStableAndReadOnlyAfterLaterWrites() {
		var tracker = new AuthoredLineTracker();
		string path = FilePath("file.txt");
		tracker.OnWrite(path, "first");
		var first = RequireSnapshot(tracker, path);

		tracker.OnWrite(path, "second");
		var second = RequireSnapshot(tracker, path);

		Assert.Equal(1, first.Version);
		Assert.Equal([new AuthoredLine(1, "first")], first.Lines);
		Assert.Equal(2, second.Version);
		Assert.Equal([new AuthoredLine(1, "second")], second.Lines);
		var lines = Assert.IsAssignableFrom<IList<AuthoredLine>>(first.Lines);
		Assert.Throws<NotSupportedException>(() => lines[0] = new AuthoredLine(1, "changed"));
	}

	[Fact]
	public void Move_ReconcilesFinalScratchContentAndTransfersItsProvenance() {
		var tracker = new AuthoredLineTracker();
		string scratch = FilePath("scratch", "untitled.txt");
		string target = FilePath("workspace", "saved.txt");
		tracker.OnRead(scratch, "seed\n");
		tracker.OnWrite(scratch, "seed\nmanual\n");
		tracker.OnRead(target, "existing target\n");

		tracker.Move(scratch, target, "seed\nmanual revised\n");

		Assert.Null(tracker.Snapshot(scratch));
		var snapshot = RequireSnapshot(tracker, target);
		Assert.Equal(4, snapshot.Version);
		Assert.Equal([new AuthoredLine(2, "manual revised")], snapshot.Lines);
	}

	[Fact]
	public void Move_WithoutAScratchMirrorMarksTheSavedContent() {
		var tracker = new AuthoredLineTracker();
		string scratch = FilePath("scratch", "untitled.txt");
		string target = FilePath("workspace", "saved.txt");

		tracker.Move(scratch, target, "saved\ncontent");

		Assert.Null(tracker.Snapshot(scratch));
		var snapshot = RequireSnapshot(tracker, target);
		Assert.Equal(2, snapshot.Version);
		Assert.Equal([
			new AuthoredLine(1, "saved"),
			new AuthoredLine(2, "content"),
		], snapshot.Lines);
	}

	[Fact]
	public void Forget_RemovesDiscardedScratchState() {
		var tracker = new AuthoredLineTracker();
		string path = FilePath("scratch", "untitled.txt");
		tracker.OnWrite(path, "manual");

		tracker.Forget(path);

		Assert.Null(tracker.Snapshot(path));
	}

	private static AuthoredLineSnapshot RequireSnapshot(AuthoredLineTracker tracker, string path) =>
		Assert.IsType<AuthoredLineSnapshot>(tracker.Snapshot(path));

	private static string FilePath(params string[] parts) =>
		Path.Combine([Path.GetTempPath(), "weavie-authored-line-tests", Guid.NewGuid().ToString("N"), .. parts]);
}
