using Weavie.Core.Agents;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpSideAttachmentTests {
	[Theory]
	[InlineData("image", true)]
	[InlineData("", true)]
	[InlineData("", false)]
	public async Task AsideKeepsTheSubmittedImageAndIdentityInItsOwnTranscript(string prompt, bool embeddedContext) {
		await using var fixture = AcpAgentSessionFixture.CreateWithEmbeddedContext(embeddedContext);
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
		Assert.Equal("image/png", image.MediaType);
		Assert.Equal(Convert.ToBase64String(await File.ReadAllBytesAsync(path)), image.MediaData);
		Assert.Null(image.Text);
		Assert.Equal(completed.ConversationId, image.ConversationId);
		Assert.Equal("end_turn", completed.Status);
		var request = Assert.Single(AcpPromptAssertions.Read(fixture));
		AcpPromptAssertions.SideScope(request, prompt);
		var wireImage = Assert.Single(AcpPromptAssertions.Blocks(request),
			block => block.GetProperty("type").GetString() == "image");
		Assert.Equal("image/png", wireImage.GetProperty("mimeType").GetString());
		Assert.Equal(Convert.ToBase64String(await File.ReadAllBytesAsync(path)),
			wireImage.GetProperty("data").GetString());
		if (prompt.Length > 0) {
			var question = Assert.Single(fixture.Messages, message => message.Type == "user-message");
			Assert.Equal("side-submission", question.ItemId);
			Assert.Equal(prompt, question.Text);
			Assert.Equal(completed.ConversationId, question.ConversationId);
			Assert.Contains(fixture.Messages, message => message.Type == "item-completed"
				&& message.Text == "image=True" && message.ConversationId == completed.ConversationId);
		} else Assert.DoesNotContain(fixture.Messages, message => message.Type == "user-message");
	}
}
