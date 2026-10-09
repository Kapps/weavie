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
			// The connection logs the cancellation on its reader thread as it hands it to the conversation,
			// whose locks it then waits on while the card is still publishing.
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
			while (!fixture.Events.Logs.Any(line => line.Contains("agent cancelled request", StringComparison.Ordinal))) {
				await Task.Delay(10, timeout.Token);
			}
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
