using Weavie.Core.Agents;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpSideAttachmentTests {
	[Theory]
	[InlineData("image")]
	[InlineData("")]
	public async Task AsideKeepsTheSubmittedImageAndIdentityInItsOwnTranscript(string prompt) {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		string path = Path.Combine(fixture.Workspace, "attachment.png");
		await File.WriteAllBytesAsync(path, [0x89, 0x50, 0x4e, 0x47, 1, 2, 3]);
		fixture.Session.AskAside(new AgentTurnSubmission {
			Id = "side-submission",
			Text = prompt,
			Kind = AgentTurnSubmissionKind.Prompt,
			CommandName = string.Empty,
			Attachments = [new AgentInputAttachment { Id = "side-image", Mime = "image/png", Path = path }],
		});

		var completed = await fixture.WaitForMessageAsync(message =>
			message.Type == "turn-completed" && message.ConversationId is not null);
		var image = Assert.Single(fixture.Messages, message => message.Type == "user-image");
		Assert.Equal("side-image", image.ItemId);
		Assert.Equal(path, image.Text);
		Assert.Equal(completed.ConversationId, image.ConversationId);
		Assert.Equal("end_turn", completed.Status);
		if (prompt.Length > 0) {
			var question = Assert.Single(fixture.Messages, message => message.Type == "user-message");
			Assert.Equal("side-submission", question.ItemId);
			Assert.Equal(completed.ConversationId, question.ConversationId);
			Assert.Contains(fixture.Messages, message => message.Type == "item-completed"
				&& message.Text == "image=True" && message.ConversationId == completed.ConversationId);
		}
	}
}
