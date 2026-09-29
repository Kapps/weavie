using Weavie.Core.Changes;
using Xunit;

namespace Weavie.Hosting.Tests;

[Collection(TestCollections.HostIntegration)]
public sealed class ReviewDecisionTests {
	[Theory]
	[InlineData("keepHunk")]
	[InlineData("keepFile")]
	[InlineData("revertHunk")]
	[InlineData("revertFile")]
	public async Task CompletedDecision_ReturnsItsOwnOutcome(string operation) {
		await using var host = await TestHost.StartAsync();
		var session = host.SelectedSession;
		string path = Path.Combine(host.RepoRoot, "readme.txt");
		session.Changes.CaptureBaseline(path);
		File.WriteAllText(path, "hello\nworld\n");
		session.Changes.RecordChange(path);
		string next = AddPendingFile(host, "next.txt");

		var result = await host.SessionRequestAsync<ReviewDecisionNavigation>(session, "review", operation, new {
			path,
			baselineStart = 2,
			baselineEndExclusive = 2,
			currentStart = 2,
			currentEndExclusive = 3,
			guardText = "world",
		});

		Assert.False(result.SourceDeleted);
		Assert.Equal(new ReviewDecisionLocation(next, 1), result.Next);
		Assert.Equal(operation.StartsWith("keep", StringComparison.Ordinal) ? "hello\nworld\n" : "hello\n", File.ReadAllText(path));
	}

	[Theory]
	[InlineData("keepHunk", "new", true)]
	[InlineData("revertHunk", "new", true)]
	[InlineData("keepHunk", "stale", false)]
	[InlineData("revertHunk", "stale", false)]
	public async Task PendingOrRejectedDecision_DoesNotGrantAdvancement(string operation, string guardText, bool applied) {
		await using var host = await TestHost.StartAsync();
		var session = host.SelectedSession;
		string path = Path.Combine(host.RepoRoot, "readme.txt");
		session.Changes.CaptureBaseline(path);
		File.WriteAllText(path, "new\nhello\nworld\n");
		session.Changes.RecordChange(path);

		var result = await host.SessionRequestAsync<ReviewDecisionNavigation>(session, "review", operation, new {
			path,
			baselineStart = 1,
			baselineEndExclusive = 1,
			currentStart = 1,
			currentEndExclusive = 2,
			guardText,
		});

		Assert.Equal(ReviewDecisionNavigation.None, result);
		if (!applied) Assert.Equal("new\nhello\nworld\n", File.ReadAllText(path));
	}

	[Theory]
	[InlineData("revertHunk")]
	[InlineData("revertFile")]
	public async Task RevertingCreatedFile_ReturnsCompletionEvenWhenItsProjectionDeletesTheSource(string operation) {
		await using var host = await TestHost.StartAsync();
		var session = host.SelectedSession;
		string path = Path.Combine(host.RepoRoot, "created.txt");
		session.Changes.CaptureBaseline(path);
		File.WriteAllText(path, "created\n");
		session.Changes.RecordChange(path);
		string next = AddPendingFile(host, "next.txt");

		var result = await host.SessionRequestAsync<ReviewDecisionNavigation>(session, "review", operation, new {
			path,
			baselineStart = 1,
			baselineEndExclusive = 1,
			currentStart = 1,
			currentEndExclusive = 2,
			guardText = "created",
		});

		Assert.True(result.SourceDeleted);
		Assert.Equal(new ReviewDecisionLocation(next, 1), result.Next);
		Assert.False(File.Exists(path));
		await session.FileActivity.DrainAsync(CancellationToken.None);
	}

	[Fact]
	public async Task KeepingAnUnchangedFile_DoesNotClaimACompletedDecision() {
		await using var host = await TestHost.StartAsync();
		var session = host.SelectedSession;
		string path = Path.Combine(host.RepoRoot, "readme.txt");
		session.Changes.CaptureBaseline(path);
		session.Changes.RecordChange(path);
		var result = await host.SessionRequestAsync<ReviewDecisionNavigation>(session, "review", "keepFile", new { path });
		Assert.Equal(ReviewDecisionNavigation.None, result);
	}

	private static string AddPendingFile(TestHost host, string name) {
		string path = Path.Combine(host.RepoRoot, name);
		host.SelectedSession.Changes.CaptureBaseline(path);
		File.WriteAllText(path, "pending\n");
		host.SelectedSession.Changes.RecordChange(path);
		return path;
	}
}
