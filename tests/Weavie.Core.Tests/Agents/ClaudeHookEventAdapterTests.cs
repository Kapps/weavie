using Weavie.Core.Agents;
using Weavie.Core.Agents.Claude;
using Weavie.Core.Hooks;
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
}
