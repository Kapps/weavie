using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpNoticeTests {
	[Fact]
	public async Task NoticesRenderAsTheirSeverityNeverAsAReply() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Submit("notices");

		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		var notices = fixture.Messages.Where(message => message.Summary?.EndsWith(" title", StringComparison.Ordinal) == true).ToArray();
		Assert.Equal(["notice", "warning", "error"], notices.Select(message => message.Type));
		Assert.Equal(["info detail", "warning detail", "error detail"], notices.Select(message => message.Text));
		Assert.All(notices, notice => Assert.Equal("1", notice.TurnId));
		Assert.DoesNotContain(fixture.Messages, message => message.ItemType == "agentMessage");
	}
}
