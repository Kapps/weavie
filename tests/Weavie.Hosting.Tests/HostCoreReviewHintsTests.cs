using System.Text.Json;
using Weavie.Core;
using Weavie.Core.Agents;
using Weavie.Core.Changes;
using Weavie.Core.Commands;
using Weavie.Core.Review.Hints;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>The `reviewHints` feature, Clear Review Hints, and the shared Fix action. See docs/specs/inline-review-hints.md.</summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreReviewHintsTests {
	[Fact]
	public async Task ReportedFindingsArePublishedServedDismissedAndCleared() {
		await using var host = await TestHost.StartAsync();
		var session = host.WorkspaceSession;
		string file = Path.Combine(session.WorkspaceRoot, "notes.txt");
		File.WriteAllText(file, "alpha\nbeta\n");

		session.Events.Observe(new AgentReviewFindingsReported([
			new ReviewFinding("notes.txt", 2, ReviewHintSeverity.Warn, "Beta is wrong", "Because.", "logic"),
			new ReviewFinding("notes.txt", 1, ReviewHintSeverity.Nit, "Alpha", string.Empty, null),
		], null));

		var hints = await HintsAsync(host, 2);
		var hint = hints[0];
		Assert.Equal(file, hint.GetProperty("path").GetString());
		Assert.Equal(2, hint.GetProperty("line").GetInt32());
		Assert.Equal("warn", hint.GetProperty("severity").GetString());
		Assert.Equal("beta", hint.GetProperty("quote").GetString());
		Assert.Equal("logic", hint.GetProperty("category").GetString());
		Assert.Equal(JsonValueKind.Null, hints[1].GetProperty("category").ValueKind);

		var source = await host.SessionRequestAsync<JsonElement>(
			session, "reviewHints", "source", new { path = file, hash = hint.GetProperty("sourceHash").GetString() });
		Assert.Equal("alpha\nbeta\n", source.GetProperty("text").GetString());

		await host.SessionRequestAsync<JsonElement>(session, "reviewHints", "dismiss", new { id = hint.GetProperty("id").GetInt64() });
		Assert.Equal("Alpha", (await HintsAsync(host, 1))[0].GetProperty("title").GetString());

		var cleared = await host.InvokeCommandAsync(session.SlotId, CoreCommands.ReviewHintsClear, new { }, CancellationToken.None);
		Assert.True(cleared.Ok, cleared.Error);
		await HintsAsync(host, 0);
	}

	[Fact]
	public async Task RejectedFindingsAreToasted() {
		await using var host = await TestHost.StartAsync();

		host.WorkspaceSession.Events.Observe(new AgentReviewFindingsReported(
			[new ReviewFinding("missing.txt", 1, ReviewHintSeverity.Bug, "Gone", string.Empty, null)], null));

		await Wait.ForAsync(() => host.Bridge.PostedEvents("notifications", "show")
			.Any(toast => toast.ToString().Contains("missing.txt", StringComparison.Ordinal)) ? true : (bool?)null);
	}

	[Fact]
	public async Task UnreadableSavedHintsAreDiscardedAndToastedWhenThePageSyncs() {
		await using var host = await TestHost.StartAsync(repo => {
			string review = Path.Combine(WeaviePaths.WorkspaceDir(WorkspaceId.ForPath(repo)), "review");
			new ReviewPersistence(Path.Combine(review, "hints.db")).Save(new Dictionary<string, string?> { ["stray"] = "x" });
		});

		Assert.Equal(0, (await HintsAsync(host, 0)).GetArrayLength());
		await Wait.ForAsync(() => host.Bridge.PostedEvents("notifications", "show")
			.Any(toast => toast.GetProperty("message").GetString() == "Saved review hints couldn't be read and were discarded: Review hint metadata is missing.")
				? true
				: (bool?)null);
	}

	[Fact]
	public async Task FixLineSubmitsAPromptToTheAgent() {
		await using var host = await TestHost.StartAsync();
		var session = host.WorkspaceSession;
		session.Claude!.EnsureStarted();
		var terminal = Assert.Single(host.Platform.NoopLauncher.Created);

		var result = await host.InvokeCommandAsync(session.SlotId, CoreCommands.AgentFixLine, new {
			path = Path.Combine(session.WorkspaceRoot, "src", "a.cs"),
			line = 3,
			quote = "return x;",
			text = "x can be null",
			instruction = "use a guard",
		}, CancellationToken.None);

		Assert.True(result.Ok, result.Error);
		Assert.Equal(
			"\x1b[200~Fix this review finding at `src/a.cs:3`:\n\n```\nreturn x;\n```\n\nx can be null\n\nAdditional instruction: use a guard\x1b[201~\r",
			terminal.WrittenText);
	}

	[Theory]
	[InlineData("return x;", "```")]
	[InlineData("var s = \"```\";", "````")]
	[InlineData("a ````` b ` c", "``````")]
	public void FixLineFencesTheQuoteBeyondItsLongestBacktickRun(string quote, string fence) =>
		Assert.Equal(fence, HostCore.CodeFence(quote));

	[Fact]
	public async Task FixLineRefusesPathsOutsideTheWorktree() {
		await using var host = await TestHost.StartAsync();

		var result = await host.InvokeCommandAsync(host.WorkspaceSession.SlotId, CoreCommands.AgentFixLine, new {
			path = Path.Combine(Path.GetTempPath(), "elsewhere.cs"),
			line = 1,
			quote = string.Empty,
			text = "x",
			instruction = string.Empty,
		}, CancellationToken.None);

		Assert.False(result.Ok);
		Assert.Contains("isn't inside", result.Error, StringComparison.Ordinal);
	}

	private static Task<JsonElement> HintsAsync(TestHost host, int count) =>
		Wait.ForAsync(() => host.Bridge.LastEvent(host.WorkspaceSession.Address, "reviewHints", "hints") is { } message
			&& message.GetProperty("hints") is var hints && hints.GetArrayLength() == count
				? hints
				: (JsonElement?)null);
}
