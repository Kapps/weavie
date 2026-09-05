using Weavie.Core.Changes;
using Weavie.Core.FileActivity;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class ChangeProjectionTests {
	[Theory]
	[InlineData("b", "")]
	[InlineData("A\nb\nC", "A\nC")]
	public void AgentEditsOverAnUntrackedRewriteConsumeOverlappingReviewRangesOnce(string after, string expected) {
		var files = new InMemoryFileSystem();
		files.WriteAllText("/w/a.txt", "original");
		var tracker = new SessionChangeTracker(files, NoopFileActivitySink.Instance, "/w", _ => true);
		tracker.CaptureBaseline("/w/a.txt");
		files.WriteAllText("/w/a.txt", "review");
		tracker.RecordChange("/w/a.txt");
		files.WriteAllText("/w/a.txt", "a\nb\nc");
		tracker.CaptureBaseline("/w/a.txt");
		files.WriteAllText("/w/a.txt", after);

		tracker.RecordChange("/w/a.txt");

		Assert.Equal(expected, Assert.Single(tracker.Changes()).CurrentText);
		Assert.Equal(after, files.ReadAllText("/w/a.txt"));
	}
}
