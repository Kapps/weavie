using Weavie.Core.Agents;
using Weavie.Core.Agents.Claude;
using Weavie.Core.Hooks;
using Xunit;

namespace Weavie.Core.Tests.Agents;

/// <summary>Claude tool calls become provider-neutral activities and phrases.</summary>
public sealed class ClaudeToolActivityTests {
	[Theory]
	[InlineData("Edit", """{"file_path":"/repo/src/App.tsx"}""", "editing App.tsx", "wants to edit App.tsx")]
	[InlineData("Read", """{"file_path":"notes.md"}""", "reading notes.md", "wants to read notes.md")]
	[InlineData("Bash", """{"command":"pnpm   test\nsecond line"}""", "running pnpm test", "wants to run pnpm test")]
	[InlineData("Grep", """{"pattern":"TODO"}""", "searching TODO", "wants to search TODO")]
	[InlineData("WebFetch", """{"url":"https://x.dev"}""", "fetching https://x.dev", "wants to fetch https://x.dev")]
	[InlineData("Task", """{"description":"Audit rail"}""", "delegating Audit rail", "wants to delegate Audit rail")]
	[InlineData("mcp__weavie__openFile", "{}", "using mcp__weavie__openFile", "wants to use mcp__weavie__openFile")]
	[InlineData("Bash", "{", "running", "wants to run")]
	public void PhrasesToolCall(string tool, string input, string doing, string wants) {
		var activity = ClaudeToolActivity.For(new HookRequest { Event = HookEventKind.PreToolUse, ToolName = tool, ToolInputJson = input });

		Assert.Equal(doing, activity.Doing);
		Assert.Equal(wants, activity.Wants);
	}

	[Fact]
	public void TitledActivityReadsAsTheProvidersOwnPhrase() {
		var activity = AgentActivity.Of(AgentActivityKind.Titled, "Run the migration");

		Assert.Equal("Run the migration", activity.Doing);
		Assert.Equal("needs approval: Run the migration", activity.Wants);
	}

	[Theory]
	[InlineData(HookEventKind.PreToolUse, false)]
	[InlineData(HookEventKind.PermissionRequest, true)]
	public void AdapterReportsActivity(HookEventKind kind, bool awaiting) {
		var events = ClaudeHookEventAdapter.Adapt(new HookRequest { Event = kind, ToolName = "Bash", ToolInputJson = """{"command":"ls"}""" });

		var observed = Assert.Single(events.OfType<AgentActivityObserved>());
		Assert.Equal(new AgentActivity(AgentActivityKind.Execute, "ls"), observed.Activity);
		Assert.Equal(awaiting, observed.AwaitingApproval);
	}
}
