using Weavie.AgentClientProtocol;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpSessionEndpointTests {
	[Fact]
	public async Task EndpointOwnsIdentityAndRejectsUseAfterRetirement() {
		await using var connection = new AcpJsonRpcConnection(new AcpAgentDefinition {
			Id = "guard",
			Name = "Guard",
			Command = "unused",
			Arguments = [],
			Environment = new Dictionary<string, string>(StringComparer.Ordinal),
			Distribution = "custom",
		}, Directory.GetCurrentDirectory(), _ => { });
		var endpoint = connection.OpenEndpoint(1, "primary", (_, _) => { }, _ => { });
		var child = connection.OpenEndpoint(1, null, (_, _) => { }, _ => { });
		var forged = new { sessionId = "another-conversation" };
		await Assert.ThrowsAsync<ArgumentException>(() => endpoint.RequestAsync("session/prompt", forged, CancellationToken.None));
		await Assert.ThrowsAsync<ArgumentException>(() => endpoint.NotifyAsync("session/cancel", forged));
		await Assert.ThrowsAsync<ArgumentException>(() => child.ForkFromAsync(endpoint, forged));
		await Assert.ThrowsAsync<ArgumentException>(() => child.CreateAsync(forged));
		endpoint.Retire();
		await Assert.ThrowsAsync<ObjectDisposedException>(() => endpoint.AuthenticateAsync("login", CancellationToken.None));
		await Assert.ThrowsAsync<ObjectDisposedException>(() => endpoint.CreateAsync(new { }));
		await Assert.ThrowsAsync<ObjectDisposedException>(() => endpoint.NotifyAsync("session/cancel", new { }));
	}
}
