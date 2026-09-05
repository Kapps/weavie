using Weavie.Core.Changes;
using Weavie.Core.FileActivity;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// The review navigator's per-file counts. Every editor save re-reads the whole turn's change list, so a file
/// whose text hasn't moved must not be diffed again — otherwise one save costs a diff of every file the agent
/// touched, on the thread the desktop hosts deliver keystrokes on.
/// </summary>
public sealed class TurnChangeSummaryTests {
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"weavie-summary-{Guid.NewGuid():N}");
	private readonly InMemoryFileSystem _fileSystem = new();
	private readonly SessionChangeTracker _tracker;

	public TurnChangeSummaryTests() {
		_tracker = new SessionChangeTracker(_fileSystem, NoopFileActivitySink.Instance, _root, _ => true);
	}

	private string Change(string name, string baseline, string current) {
		string path = Path.Combine(_root, name);
		_fileSystem.WriteAllText(path, baseline);
		_tracker.CaptureBaseline(path);
		_fileSystem.WriteAllText(path, current);
		_tracker.RecordChange(path);
		return path;
	}

	private TurnChangeSummary SummaryFor(string path) =>
		_tracker.TurnChangeSummaries().Single(summary => summary.Change.Path == path);

	[Fact]
	public void CountsTheSpanFromTheAcceptedAnchorAndLandsOnTheFirstChange() {
		string path = Change("a.ts", "one\ntwo\nthree\n", "one\nTWO\nthree\nfour\n");

		var summary = SummaryFor(path);

		Assert.Equal(2, summary.Added);
		Assert.Equal(1, summary.Removed);
		Assert.Equal(2, summary.Line);
	}

	[Fact]
	public void RereadingAnUnchangedTurnDiffsNothingAgain() {
		string first = Change("a.ts", "one\n", "one\ntwo\n");
		string second = Change("b.ts", "three\n", "three\nfour\n");

		var before = _tracker.TurnChangeSummaries();
		var after = _tracker.TurnChangeSummaries();

		// Same instances back: nothing was recomputed for either file.
		Assert.Same(before.Single(s => s.Change.Path == first), after.Single(s => s.Change.Path == first));
		Assert.Same(before.Single(s => s.Change.Path == second), after.Single(s => s.Change.Path == second));
	}

	[Fact]
	public void SavingOneFileRediffsOnlyThatFile() {
		string saved = Change("a.ts", "one\n", "one\ntwo\n");
		string untouched = Change("b.ts", "three\n", "three\nfour\n");
		var before = _tracker.TurnChangeSummaries();

		_tracker.CaptureBaseline(saved);
		_fileSystem.WriteAllText(saved, "one\ntwo\nthree\n");
		_tracker.RecordChange(saved);
		var after = _tracker.TurnChangeSummaries();

		Assert.NotSame(before.Single(s => s.Change.Path == saved), after.Single(s => s.Change.Path == saved));
		Assert.Equal(2, after.Single(s => s.Change.Path == saved).Added);
		Assert.Same(before.Single(s => s.Change.Path == untouched), after.Single(s => s.Change.Path == untouched));
	}

	[Fact]
	public void AFileLeavingTheTurnIsSummarizedAfreshWhenItReturns() {
		string path = Change("a.ts", "one\n", "one\ntwo\n");
		_ = _tracker.TurnChangeSummaries();

		// Keep-all empties the turn, so the memo has nothing to hold.
		_tracker.AcceptTurn();
		Assert.Empty(_tracker.TurnChangeSummaries());

		_tracker.CaptureBaseline(path);
		_fileSystem.WriteAllText(path, "one\ntwo\nthree\nfour\n");
		_tracker.RecordChange(path);
		var summary = Assert.Single(_tracker.TurnChangeSummaries());
		Assert.Equal(2, summary.Added);
	}
}
