using Weavie.Core.Agents;
using Weavie.Core.Changes;
using Weavie.Core.FileActivity;
using Weavie.Core.FileSystem;
using Weavie.Core.Review;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Disk changes reach the review through observed paths, and review actions read only the files they write: keeping
/// reads nothing, so its cost never grows with the review.
/// </summary>
public sealed class SessionChangeTrackerDiskObservationTests {
	private static readonly string Root = Path.GetFullPath("/w");
	private static readonly ReviewContext Review = new(0, "vs main", "", "base", "head", null, Root, "main", "topic");

	[Fact]
	public void KeepingAndClosingReadNoFile() {
		var (files, tracker, paths) = ArmedReview(50);

		files.Reads.Clear();
		tracker.KeepFile(paths[0]);
		Assert.Empty(files.Reads);
		tracker.CloseReview();
		Assert.Empty(files.Reads);
		Assert.Empty(tracker.TurnChanges());
	}

	[Fact]
	public void KeepingTheLastFileReadsTheReviewBeforeClosingIt() {
		var (files, tracker, paths) = ArmedReview(2);
		tracker.KeepFile(paths[1]);
		files.WriteAllText(paths[1], "edited outside, not yet observed\n");

		tracker.KeepFile(paths[0]);

		Assert.NotNull(tracker.Review);
		Assert.Equal("edited outside, not yet observed\n", tracker.GetTurn(paths[1])!.CurrentText);
	}

	[Fact]
	public void RevertingReadsOnlyTheFileItWrites() {
		var (files, tracker, paths) = ArmedReview(50);

		files.Reads.Clear();
		tracker.RevertFile(paths[3]);
		Assert.Equal([paths[3]], files.Reads.Distinct(PathIdentity.Comparer));
	}

	[Fact]
	public void AnObservedOutsideEditUpdatesOnlyItsFile() {
		var (files, tracker, paths) = ArmedReview(5);
		files.WriteAllText(paths[1], "edited outside\n");
		files.WriteAllText(Path.Combine(Root, "untracked.txt"), "new\n");

		files.Reads.Clear();
		var changed = tracker.ObserveDisk([paths[1], Path.Combine(Root, "untracked.txt")]);

		Assert.Equal([paths[1]], changed);
		Assert.Equal([paths[1]], files.Reads.Distinct(PathIdentity.Comparer));
		Assert.Equal("edited outside\n", tracker.GetTurn(paths[1])!.CurrentText);
		Assert.Empty(tracker.ObserveDisk([paths[1]]));
	}

	[Fact]
	public void AnObservedDeletionRetainsTheFileAsMissing() {
		var (files, tracker, paths) = ArmedReview(2);
		files.DeleteFile(paths[0]);

		Assert.Equal([paths[0]], tracker.ObserveDisk([paths[0]]));
		Assert.False(tracker.GetTurn(paths[0])!.CurrentExists);
	}

	[Fact]
	public void AnAgentEditInProgressIsLeftToItsCompletion() {
		var files = new CountingFileSystem();
		string path = Path.Combine(Root, "agent.txt");
		files.WriteAllText(path, "before\n");
		var tracker = Tracker(files);
		var mutation = new AgentMutation.File(path, Cwd: null, ProvidesEditLocation: true);

		tracker.Observe(new AgentToolStarting(mutation));
		files.WriteAllText(path, "agent wrote\n");
		Assert.Empty(tracker.ObserveDisk([path]));
		tracker.Observe(new AgentToolCompleted(mutation));

		var change = Assert.Single(tracker.TurnChanges());
		Assert.Equal("agent wrote\n", change.CurrentText);
		Assert.Empty(tracker.ObserveDisk([path]));
	}

	[Fact]
	public void AnEditLeftUnfinishedIsReadWhenItsTurnEnds() {
		var (files, tracker, path) = AgentEditing();
		tracker.Observe(new AgentTurnStopped(WillResume: true));
		Assert.Empty(tracker.ObserveDisk([path]));

		tracker.Observe(new AgentConversationEvent("side", new AgentTurnStopped(WillResume: false)));
		Assert.Empty(tracker.ObserveDisk([path]));

		tracker.Observe(new AgentTurnStopped(WillResume: false));
		Assert.Equal("agent wrote\n", tracker.GetTurn(path)!.CurrentText);
		files.WriteAllText(path, "edited outside\n");
		Assert.Equal([path], tracker.ObserveDisk([path]));
	}

	[Fact]
	public async Task AnObservationDuringAnEditorSaveLeavesTheFileToTheSave() {
		var (files, tracker, path) = AgentEditing();
		tracker.Observe(new AgentToolCompleted(new AgentMutation.File(path, Cwd: null, ProvidesEditLocation: true)));
		Task<IReadOnlyList<string>>? observed = null;

		tracker.SaveHandEdit(path, "MINE\nagent wrote\n", () => {
			files.WriteAllText(path, string.Empty);
			observed = Task.Run(() => tracker.ObserveDisk([path]));
			Thread.Sleep(100);
			files.WriteAllText(path, "MINE\nagent wrote\n");
			return true;
		});

		Assert.Empty(await observed!);
		Assert.Equal("agent wrote\n", Assert.Single(tracker.TurnChanges()).CurrentText);
	}

	[Fact]
	public void AStalledSaveBlocksNothingElse() {
		var (files, tracker, paths) = ArmedReview(2);
		files.WriteAllText(paths[1], "edited outside\n");
		bool observedMeanwhile = false;

		tracker.SaveHandEdit(paths[0], "saved\n", () => {
			observedMeanwhile = Task.Run(() => tracker.ObserveDisk([paths[1]])).Wait(TimeSpan.FromSeconds(5));
			files.WriteAllText(paths[0], "saved\n");
			return true;
		});

		Assert.True(observedMeanwhile);
		Assert.Equal("edited outside\n", tracker.GetTurn(paths[1])!.CurrentText);
	}

	private static (CountingFileSystem Files, SessionChangeTracker Tracker, string Path) AgentEditing() {
		var files = new CountingFileSystem();
		string path = Path.Combine(Root, "agent.txt");
		files.WriteAllText(path, "before\n");
		var tracker = Tracker(files);
		tracker.Observe(new AgentToolStarting(new AgentMutation.File(path, Cwd: null, ProvidesEditLocation: true)));
		files.WriteAllText(path, "agent wrote\n");
		return (files, tracker, path);
	}

	private static (CountingFileSystem Files, SessionChangeTracker Tracker, string[] Paths) ArmedReview(int count) {
		var files = new CountingFileSystem();
		string[] paths = [.. Enumerable.Range(0, count).Select(index => Path.Combine(Root, $"f{index}.txt"))];
		foreach (string path in paths) files.WriteAllText(path, $"proposal {path}\n");
		var tracker = Tracker(files);
		tracker.ArmReview(Review, [.. paths.Select(path => new ReviewSeed(path, "old\n", $"proposal {path}\n", true, true))]);
		return (files, tracker, paths);
	}

	private static SessionChangeTracker Tracker(IFileSystem files) =>
		new(files, NoopFileActivitySink.Instance, Root, _ => true, SessionChangeTracker.NothingIgnored, new MemoryReviewPersistence());

	// Records every path whose content or existence is read.
	private sealed class CountingFileSystem : IFileSystem {
		private readonly InMemoryFileSystem _files = new();

		public List<string> Reads { get; } = [];

		public bool FileExists(string path) { Reads.Add(path); return _files.FileExists(path); }
		public bool DirectoryExists(string path) => _files.DirectoryExists(path);
		public bool TryGetStat(string path, out FileStat stat) { Reads.Add(path); return _files.TryGetStat(path, out stat); }
		public IReadOnlyList<DirectoryEntry> EnumerateDirectory(string path) => _files.EnumerateDirectory(path);
		public string ReadAllText(string path) { Reads.Add(path); return _files.ReadAllText(path); }
		public bool TryReadAllText(string path, out string contents) { Reads.Add(path); return _files.TryReadAllText(path, out contents); }
		public byte[] ReadAllBytes(string path) { Reads.Add(path); return _files.ReadAllBytes(path); }
		public void WriteAllText(string path, string contents) => _files.WriteAllText(path, contents);
		public void WriteAllBytes(string path, byte[] contents) => _files.WriteAllBytes(path, contents);
		public void AppendAllText(string path, string contents) => _files.AppendAllText(path, contents);
		public void WriteAllTextAtomic(string path, string contents) => _files.WriteAllTextAtomic(path, contents);
		public void DeleteFile(string path) => _files.DeleteFile(path);
	}
}
