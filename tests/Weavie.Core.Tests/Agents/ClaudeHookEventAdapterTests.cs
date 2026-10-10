using Weavie.Core.Agents;
using Weavie.Core.Agents.Claude;
using Weavie.Core.Hooks;
using Weavie.Core.Review.Hints;
using Xunit;

namespace Weavie.Core.Tests.Agents;

/// <summary>Claude hook vocabulary is translated at the provider boundary.</summary>
public sealed class ClaudeHookEventAdapterTests {
	[Fact]
	public void EditPreToolUse_MapsMutationAndDisposition() {
		var request = new HookRequest {
			Event = HookEventKind.PreToolUse,
			ToolName = "Edit",
			ToolInputJson = """{"file_path":"src/a.cs"}""",
			Cwd = "/repo",
			PermissionMode = "acceptEdits",
		};

		var events = ClaudeHookEventAdapter.Adapt(request);

		var started = Assert.IsType<AgentToolStarting>(events[0]);
		var mutation = Assert.IsType<AgentMutation.File>(started.Mutation);
		Assert.Equal("src/a.cs", mutation.Path);
		Assert.Equal("/repo", mutation.Cwd);
		Assert.True(mutation.ProvidesEditLocation);
		Assert.Equal("acceptEdits", Assert.IsType<AgentEditDispositionObserved>(events[1]).Disposition);
	}

	[Fact]
	public void MalformedEditInput_IsNotTrackable() {
		var request = new HookRequest {
			Event = HookEventKind.PostToolUse,
			ToolName = "Write",
			ToolInputJson = "{",
		};

		var completed = Assert.IsType<AgentToolCompleted>(Assert.Single(ClaudeHookEventAdapter.Adapt(request)));
		Assert.IsType<AgentMutation.None>(completed.Mutation);
	}

	[Fact]
	public void Stop_PreservesPendingResumption() {
		var request = new HookRequest {
			Event = HookEventKind.Stop,
			ToolName = string.Empty,
			ToolInputJson = "{}",
			SessionWillResume = true,
		};

		Assert.True(Assert.IsType<AgentTurnStopped>(Assert.Single(ClaudeHookEventAdapter.Adapt(request))).WillResume);
	}

	[Fact]
	public void ReportFindingsPostToolUse_MapsFindingsAndSeverity() {
		var events = ClaudeHookEventAdapter.Adapt(Findings(HookEventKind.PostToolUse, """
			{"findings":[
				{"file":"a.cs","line":3,"summary":"Null deref","failure_scenario":"Crashes on empty input","category":"correctness"},
				{"file":"b.cs","line":7,"summary":"Long summary","short_summary":"Short","failure_scenario":"Races","verdict":"PLAUSIBLE"},
				{"file":"c.cs","line":1,"summary":"Off by one","failure_scenario":"Skips last","verdict":"CONFIRMED"}]}
			"""));

		var reported = Assert.IsType<AgentReviewFindingsReported>(events[^1]);
		Assert.Equal("/repo", reported.Cwd);
		Assert.Equal([
			new ReviewFinding("a.cs", 3, ReviewHintSeverity.Bug, "Null deref", "Crashes on empty input", "correctness"),
			new ReviewFinding("b.cs", 7, ReviewHintSeverity.Warn, "Short", "Long summary\n\nRaces", null),
			new ReviewFinding("c.cs", 1, ReviewHintSeverity.Bug, "Off by one", "Skips last", null),
		], reported.Findings);
	}

	[Fact]
	public void ReportFindingsPreToolUse_ReportsNothing() =>
		Assert.DoesNotContain(
			ClaudeHookEventAdapter.Adapt(Findings(HookEventKind.PreToolUse, """{"findings":[{"file":"a.cs","line":3,"summary":"x"}]}""")),
			value => value is AgentReviewFindingsReported or AgentReviewFindingsUnreadable);

	[Theory]
	[InlineData("{")]
	[InlineData("""{"findings":{}}""")]
	[InlineData("""{"findings":[{"file":"a.cs","summary":"x"}]}""")]
	[InlineData("""{"findings":[{"file":"a.cs","line":3,"summary":"x","verdict":"MAYBE"}]}""")]
	public void MalformedReportFindings_IsSurfaced(string input) =>
		Assert.IsType<AgentReviewFindingsUnreadable>(ClaudeHookEventAdapter.Adapt(Findings(HookEventKind.PostToolUse, input))[^1]);

	private static HookRequest Findings(HookEventKind kind, string input) => new() {
		Event = kind,
		ToolName = "ReportFindings",
		ToolInputJson = input,
		Cwd = "/repo",
	};
}
