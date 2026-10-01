using Weavie.Core.Review;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class ReviewThreadTests {
	[Fact]
	public void Group_ThreadsByRootNotByLine() {
		var threads = ReviewThread.Group([
			Comment(1, 0, "2026-01-01", line: 5),
			Comment(2, 0, "2026-01-02", line: 5),
			Comment(3, 1, "2026-01-03", line: 5),
		]);

		Assert.Equal([1L, 2L], threads.Select(t => t.RootId));
		Assert.Equal([1L, 3L], threads[0].Comments.Select(c => c.Id));
		Assert.Equal([2L], threads[1].Comments.Select(c => c.Id));
	}

	[Fact]
	public void Group_PutsTheRootFirstAndRepliesInCreationOrder() {
		var threads = ReviewThread.Group([
			Comment(12, 10, "2026-01-05", line: 0),
			Comment(11, 10, "2026-01-02", line: 0),
			Comment(10, 0, "2026-01-01", line: 0, outdated: true),
			Comment(13, 11, "2026-01-03", line: 0),
		]);

		var thread = Assert.Single(threads);
		Assert.Equal([10L, 11L, 13L, 12L], thread.Comments.Select(c => c.Id));
		Assert.True(thread.Outdated);
		Assert.Equal(0, thread.Line);
	}

	[Fact]
	public void Group_AReplyWhoseRootIsGoneRootsItsOwnThread() {
		var thread = Assert.Single(ReviewThread.Group([Comment(4, 99, "2026-01-01", line: 7)]));

		Assert.Equal(4, thread.RootId);
		Assert.Equal(7, thread.Line);
	}

	private static ReviewComment Comment(long id, long inReplyTo, string createdAt, int line) =>
		Comment(id, inReplyTo, createdAt, line, outdated: false);

	private static ReviewComment Comment(long id, long inReplyTo, string createdAt, int line, bool outdated) => new() {
		Id = id,
		Path = "src/a.ts",
		Line = line,
		Outdated = outdated,
		Side = "right",
		Author = "ann",
		AuthorAvatarUrl = string.Empty,
		Url = string.Empty,
		Body = $"comment {id}",
		CreatedAt = createdAt,
		UpdatedAt = createdAt,
		InReplyTo = inReplyTo,
	};
}
