using Weavie.Core.Agents;
using Weavie.Core.Changes;
using Weavie.Core.FileActivity;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class ReviewDecisionNavigationTests {
	private readonly InMemoryFileSystem _files = new();
	private readonly SessionChangeTracker _tracker;
	private readonly string _root = OperatingSystem.IsWindows() ? @"C:\review" : "/review";

	public ReviewDecisionNavigationTests() {
		_tracker = new(_files, NoopFileActivitySink.Instance, _root, _ => true);
		_tracker.Observe(new AgentPromptSubmitted("session", "update files"));
	}

	[Fact]
	public void NextPending_SkipsKeptFilesAndPreservesAuthoritativeLine() {
		string first = Edit("first.txt", "before\n", "after\n");
		string kept = Edit("kept.txt", "before\n", "after\n");
		string next = Edit("next.txt", "one\nbefore\n", "one\nafter\n");
		_tracker.KeepFile(kept);
		Assert.True(_tracker.KeepFile(first, out var navigation));
		Assert.Equal(new ReviewDecisionNavigation(false, new(next, 2)), navigation);
	}

	[Fact]
	public void NextPending_WrapsAndRecognizesAnEmptyCreatedFile() {
		string first = Path.Combine(_root, "empty.txt");
		_tracker.CaptureBaseline(first);
		_files.WriteAllText(first, "");
		_tracker.RecordChange(first);
		string last = Edit("last.txt", "before\n", "after\n");
		Assert.True(_tracker.KeepFile(last, out var navigation));
		Assert.Equal(new ReviewDecisionNavigation(false, new(first, 1)), navigation);
		Assert.True(_tracker.KeepFile(first, out var finished));
		Assert.Equal(ReviewDecisionNavigation.None, finished);
	}

	[Fact]
	public void SourceDeletion_IsGrantedOnlyByTheDeletingDecision() {
		string source = Path.Combine(_root, "created.txt");
		_tracker.CaptureBaseline(source);
		_files.WriteAllText(source, "created\n");
		_tracker.RecordChange(source);
		string next = Edit("next.txt", "before\n", "after\n");
		Assert.Equal(RevertHunkOutcome.Deleted, _tracker.RevertFile(source, out var navigation));
		Assert.Equal(new ReviewDecisionNavigation(true, new(next, 1)), navigation);
		Assert.False(_files.FileExists(source));
		Assert.False(_tracker.KeepFile(source, out var unchanged));
		Assert.Equal(ReviewDecisionNavigation.None, unchanged);
	}

	[Fact]
	public void NavigationIsCapturedBeforePostMutationObserversCanChangeAnotherFile() {
		string source = Edit("source.txt", "before\n", "after\n");
		string next = Edit("next.txt", "before\n", "after\n");
		Edit("later.txt", "before\n", "after\n");
		bool observed = false;
		_tracker.Corrected += _ => {
			observed = true;
			_tracker.KeepFile(next);
		};
		Assert.Equal(RevertHunkOutcome.Reverted, _tracker.RevertFile(source, out var navigation));
		Assert.True(observed);
		Assert.Equal(new ReviewDecisionNavigation(false, new(next, 1)), navigation);
		var turn = _tracker.GetTurn(next)!;
		Assert.Equal(turn.BaselineText, turn.CurrentText);
	}

	private string Edit(string name, string baseline, string current) {
		string path = Path.Combine(_root, name);
		_files.WriteAllText(path, baseline);
		var mutation = new AgentMutation.File(path, Cwd: null, ProvidesEditLocation: true);
		_tracker.Observe(new AgentToolStarting(mutation));
		_files.WriteAllText(path, current);
		_tracker.Observe(new AgentToolCompleted(mutation));
		return path;
	}
}
