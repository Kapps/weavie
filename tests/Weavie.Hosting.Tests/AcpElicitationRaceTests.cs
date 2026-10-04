using Weavie.Core.Agents;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpElicitationRaceTests {
	[Fact]
	public async Task CancellationArrivingWhileTheInputCardPublishesSettlesTheRequest() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: false, persistedSessionId: null);
		await fixture.StartAsync();
		var publishing = fixture.Events.BlockNext<AgentInputRequested>();

		fixture.Submit("input-cancel-race");
		try {
			await publishing.Entered.WaitAsync(TimeSpan.FromSeconds(10));
			await File.WriteAllTextAsync(Path.Combine(fixture.Workspace, "input-publishing"), string.Empty);
			string sent = Path.Combine(fixture.Workspace, "input-cancel-sent");
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
			while (!File.Exists(sent)) await Task.Delay(10, timeout.Token);
			// Gives the host's reader time to dispatch the cancellation while the card is still publishing.
			await Task.Delay(TimeSpan.FromMilliseconds(200));
		} finally {
			publishing.Release();
		}

		var resolved = await fixture.WaitForMessageAsync(message => message.Type == "input-resolved");
		Assert.Equal("cancelled", resolved.Status);
		await fixture.WaitForMessageAsync(message => message.Text == "input cancel race settled");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "1");
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}
}
