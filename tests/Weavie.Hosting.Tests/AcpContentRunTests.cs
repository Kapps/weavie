using Weavie.Core.Agents;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpContentRunTests {
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task SteeringAndNewToolsSeparateContentButOldToolUpdatesDoNot(bool sharedMessageId) {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		File.WriteAllText(Path.Combine(fixture.Workspace, "hold-steer-reply"), "");
		File.WriteAllText(Path.Combine(fixture.Workspace, "hold-steer-finish"), "");
		fixture.Submit(sharedMessageId ? "steer-visibility-shared" : "steer-visibility");
		var before = await fixture.WaitForMessageAsync(message => message.Type == "thought-message-delta");
		fixture.Submit("What agent is doing the building?");
		await fixture.WaitForMessageAsync(message => message.Type == "user-steer");
		File.WriteAllText(Path.Combine(fixture.Workspace, "release-steer-reply"), "");
		await fixture.WaitForMessageAsync(message => message.Text == "Checking the build result.");
		var reply = Assert.Single(fixture.Messages, message => message.Type == "item-completed"
			&& message.Text == "I'm running the build directly in this session.");
		Assert.NotEqual(before.ItemId, reply.ItemId);
		Assert.Equal(sharedMessageId ? "shared-response" : null, reply.MessageId);
		var checking = fixture.Messages.Last(message => message.Type == "thought-message-delta");
		Assert.NotEqual(reply.ItemId, checking.ItemId);
		Assert.True(Array.FindIndex(fixture.Messages.ToArray(), message => message.Type == "user-steer")
			< Array.FindIndex(fixture.Messages.ToArray(), message => message.ItemId == reply.ItemId));
		File.WriteAllText(Path.Combine(fixture.Workspace, "release-steer-finish"), "");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		var saved = fixture.Sessions.ReadMessages("fake", fixture.Workspace);
		var restored = new AgentPaneLedger();
		foreach (var message in saved) restored.Store(message);
		var content = Enumerable.Range(0, restored.Count).Select(index => restored.EntryAt(index).Message)
			.Where(message => message.ItemType is "thought" or "agentMessage").ToArray();
		Assert.Equal([
			"Inspecting the build before your question.", "I'm running the build directly in this session.",
			"Checking the build result.", "Build verified.",
		], content.Select(message => message.Text));
		Assert.All(content, message => Assert.Equal("item-completed", message.Type));
		Assert.Equal(4, content.Select(message => message.ItemId).Distinct().Count());
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
	}
	[Fact]
	public async Task PermissionIntroductionEndsContentButItsExecutionNotificationDoesNot() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: false, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Submit("permission-lifecycle:content");
		var permission = await fixture.WaitForMessageAsync(message => message.Type == "approval-requested");
		await fixture.WaitForMessageAsync(message => message.Text == "During permission");
		Assert.Contains(fixture.Messages, message => message.Type == "item-completed" && message.Text == "Before permission.");
		fixture.Session.ResolvePermission(permission.RequestId!, "allow");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		Assert.Equal(["Before permission.", "During permission and after approval."],
			fixture.Messages.Where(message => message.Type == "item-completed" && message.ItemType == "thought")
				.Select(message => message.Text));
	}

}
