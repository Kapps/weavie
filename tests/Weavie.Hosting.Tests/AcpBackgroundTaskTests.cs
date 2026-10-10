using System.Text.Json;
using System.Text.Json.Nodes;
using Weavie.Core.Agents;
using Weavie.Core.Sessions;
using Weavie.TestSupport;
using Xunit;
using static Weavie.Hosting.Tests.AcpLiveReplacementTests;

namespace Weavie.Hosting.Tests;

// Background tasks join the root's work: shell tasks are tray items, workflows also journal a card, stops go to the root.
public sealed class AcpBackgroundTaskTests {
	[Fact]
	public async Task NativePaneAdvertisesExactlyItsClientCapabilities() {
		await using var fixture = await StartedAsync();
		var initialize = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.FakeAcpStateDirectory, "initialize.json")))!;

		Assert.Equal(
			"""{"auth":{"terminal":true},"fs":{"readTextFile":true,"writeTextFile":true},"plan":{},"subagents":{},"terminal":true,"session":{"configOptions":{"boolean":{}},"notices":{}},"elicitation":{"form":{},"url":{}},"_meta":{"jetbrains":{"air":{"version":1,"capabilities":["nativeSubagentSessions","asyncTasks"]}}}}""",
			initialize["clientCapabilities"]!.ToJsonString());
	}

	[Fact]
	public async Task ShellTaskIsATrayItemWhoseBackgroundedToolNeverHoldsTheTurn() {
		await using var fixture = await StartedAsync();
		var item = await StartTaskAsync(fixture, "task-held", "1");

		Assert.Equal((AgentBackgroundKind.Task, "shell", true, null), (item.Kind, item.Type, item.CanStop, item.TranscriptItemId));
		Assert.Equal(SessionStatus.Waiting, fixture.Events.Status.Status);
		Assert.DoesNotContain(fixture.Messages, message => message.ItemType == "backgroundTask");
		Signal(fixture, "release-task");

		await Wait.UntilAsync(() => fixture.Session.BackgroundWork.Single().State == AgentBackgroundState.Completed);
		await fixture.Events.WaitForAsync(value => value is AgentBackgroundChanged { Running: false });
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
		fixture.Submit("hello");
		await fixture.WaitForMessageAsync(message => message.Type == "item-completed" && message.Text == "echo: hello");
		Assert.Empty(fixture.Session.BackgroundWork);
	}

	[Theory]
	[InlineData(false, AgentBackgroundState.Stopped)]
	[InlineData(true, AgentBackgroundState.Completed)]
	public async Task StopGoesOutOnTheRootAndALaterTerminalStateCorrectsIt(bool corrected, AgentBackgroundState final) {
		await using var fixture = await StartedAsync();
		if (corrected) Signal(fixture, "correct-stop");
		var item = await StartTaskAsync(fixture, "task-held", "1");

		Assert.True(await fixture.Session.StopBackgroundTaskAsync(item.Id));

		var notice = await fixture.WaitForMessageAsync(message => message.Summary == "Task stopped by user");
		Assert.Equal("notice", notice.Type);
		await Wait.UntilAsync(() => fixture.Session.BackgroundWork.Single().State == final);
		Assert.False(fixture.Session.BackgroundWork.Single().CanStop);
		Assert.Equal("fake-session:" + item.Id["task:".Length..], Assert.Single(Stops(fixture)));
		var tool = await fixture.WaitForMessageAsync(message => message.ItemId == "tool:" + item.Id["task:".Length..] && message.Type == "item-completed");
		Assert.Equal("cancelled", tool.Status);
	}

	[Fact]
	public async Task ATaskStartedInsideASubagentStopsThroughTheRoot() {
		await using var fixture = await StartedAsync();
		var item = await StartTaskAsync(fixture, "task-in-subagent", "1");

		Assert.True(await fixture.Session.StopBackgroundTaskAsync(item.Id));

		await Wait.UntilAsync(() => fixture.Session.BackgroundWork.Single(work => work.Id == item.Id).State == AgentBackgroundState.Stopped);
		Assert.Equal("fake-session:child-task", Assert.Single(Stops(fixture)));
	}

	[Fact]
	public async Task WorkflowJournalsACardFromSpawnToItsFinalState() {
		await using var fixture = await StartedAsync();
		var item = await StartTaskAsync(fixture, "workflow-held", "1");

		var started = await fixture.WaitForMessageAsync(message => message.ItemType == "backgroundTask" && message.Type == "item-started");
		Assert.Equal((item.Id, "1", "code-review", "workflow"), (started.ItemId, started.TurnId, started.Summary, started.Category));
		await Wait.UntilAsync(() => fixture.Session.BackgroundWork.Single().Usage is { TotalTokens: 1200 });
		Assert.Equal("Review: correctness", fixture.Session.BackgroundWork.Single().LastActivity);
		Signal(fixture, "release-workflow");

		var completed = await fixture.WaitForMessageAsync(message => message.ItemType == "backgroundTask" && message.Type == "item-completed");
		Assert.Equal(("completed", item.Id), (completed.Status, completed.ItemId));
		Assert.NotNull(completed.CompletedAtMs);
		Assert.Contains(fixture.Sessions.ReadMessages("fake", fixture.Workspace), message => message.ItemType == "backgroundTask" && message.Type == "item-completed");
	}

	[Fact]
	public async Task RestartStopsRunningTasksAndSettlesTheirCards() {
		await using var fixture = await StartedAsync();
		await StartTaskAsync(fixture, "workflow-held", "1");

		fixture.Session.Restart();

		var settled = await fixture.WaitForMessageAsync(message => message.ItemType == "backgroundTask" && message.Type == "item-completed");
		Assert.Equal("stopped", settled.Status);
	}

	[Theory]
	[InlineData("input-custom-answer", "choice", new[] { "my own" }, """{"choice":"my own"}""")]
	[InlineData("input-custom-answer-multiple", "question_0", new[] { "Alpha", "mine" }, """{"question_0":["Alpha"],"question_0_custom":"mine"}""")]
	public async Task CustomAnswerCompanionsBecomeTheirQuestionsOther(string prompt, string question, string[] answer, string content) {
		await using var fixture = await StartedAsync();
		fixture.Submit(prompt);

		var request = await fixture.WaitForMessageAsync(message => message.Type == "input-requested");
		var asked = Assert.Single(request.Questions!);
		Assert.Equal((question, true), (asked.Id, asked.AllowsOther));
		fixture.Session.ResolveInput(request.RequestId!, "accept", new Dictionary<string, IReadOnlyList<string>> { [question] = answer });

		await fixture.WaitForMessageAsync(message => message.Text == "custom answer: " + content);
	}

	private static async Task<AcpAgentSessionFixture> StartedAsync() {
		var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		return fixture;
	}

	private static async Task<AgentBackgroundItem> StartTaskAsync(AcpAgentSessionFixture fixture, string prompt, string turnId) {
		fixture.Submit(prompt);
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.ConversationId is null && message.TurnId == turnId);
		return fixture.Session.BackgroundWork.Last(item => item.Kind == AgentBackgroundKind.Task);
	}

	private static string[] Stops(AcpAgentSessionFixture fixture) => File.ReadAllLines(Path.Combine(fixture.FakeAcpStateDirectory, "stops.log"));
}
