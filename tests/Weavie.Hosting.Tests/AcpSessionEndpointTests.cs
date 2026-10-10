using System.Text.Json;
using System.Text.Json.Nodes;
using Weavie.AgentClientProtocol;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpSessionEndpointTests {
	[Fact]
	public async Task EndpointOwnsIdentityAndRejectsUseAfterRetirement() {
		await using var connection = Connection();
		var endpoint = connection.OpenEndpoint(1, _ => { }, _ => { }, _ => { });
		endpoint.Bind("primary");
		var child = connection.OpenEndpoint(1, _ => { }, _ => { }, _ => { });
		var forged = new JsonObject { ["sessionId"] = "another-conversation" };
		await Assert.ThrowsAsync<ArgumentException>(() => endpoint.RequestAsync("session/prompt", forged, CancellationToken.None));
		await Assert.ThrowsAsync<ArgumentException>(() => endpoint.NotifyAsync("session/cancel", forged));
		await Assert.ThrowsAsync<ArgumentException>(() => child.ForkFromAsync(endpoint, forged));
		await Assert.ThrowsAsync<ArgumentException>(() => child.CreateAsync(forged));
		endpoint.Retire();
		await Assert.ThrowsAsync<ObjectDisposedException>(() => endpoint.AuthenticateAsync("login", CancellationToken.None));
		await Assert.ThrowsAsync<ObjectDisposedException>(() => endpoint.CreateAsync([]));
		await Assert.ThrowsAsync<ObjectDisposedException>(() => endpoint.NotifyAsync("session/cancel", []));
	}

	[Fact]
	public async Task ChildEndpointsAreBoundOnceAndNeverGainASecondOwner() {
		await using var connection = Connection();
		var root = connection.OpenEndpoint(1, _ => { }, _ => { }, _ => { });
		root.Bind("root");
		var received = new List<string>();
		var child = root.OpenChild("child", value => received.Add(value.GetRawText()), _ => { }, _ => { });

		Assert.Equal("child", child.SessionId);
		Assert.True(child.Opened);
		Assert.Throws<AcpProtocolException>(() => root.OpenChild("child", _ => { }, _ => { }, _ => { }));
		root.Sink("child");
		child.Notify(Update("child", "agent_message_chunk"));
		Assert.Single(received);
		Assert.False(child.Retired);
	}

	[Fact]
	public async Task RetiredEndpointSinksTheSubagentsItAnnounces() {
		await using var connection = Connection();
		var root = connection.OpenEndpoint(1, _ => { }, _ => { }, _ => { });
		root.Bind("root");
		root.Retire();

		root.Notify(Spawn("root", "orphan"));
		root.Sink("replayed");

		Assert.Throws<AcpProtocolException>(() => root.OpenChild("orphan", _ => { }, _ => { }, _ => { }));
		Assert.Throws<AcpProtocolException>(() => root.OpenChild("replayed", _ => { }, _ => { }, _ => { }));
		var live = connection.OpenEndpoint(2, _ => { }, _ => { }, _ => { });
		Assert.Equal("orphan", live.OpenChild("orphan", _ => { }, _ => { }, _ => { }).SessionId);
	}

	private static AcpJsonRpcConnection Connection() => new(new AcpAgentDefinition {
		Id = "guard",
		Name = "Guard",
		Command = "unused",
		Arguments = [],
		Environment = new Dictionary<string, string>(StringComparer.Ordinal),
	}, Directory.GetCurrentDirectory(), _ => { });

	private static JsonElement Spawn(string parent, string child) => JsonSerializer.SerializeToElement(new JsonObject {
		["jsonrpc"] = "2.0",
		["method"] = "session/update",
		["params"] = new JsonObject {
			["sessionId"] = parent,
			["update"] = new JsonObject {
				["sessionUpdate"] = "subagent_spawned", ["subagentSessionId"] = child, ["name"] = "n", ["task"] = "t",
			},
		},
	});

	private static JsonElement Update(string session, string kind) => JsonSerializer.SerializeToElement(new JsonObject {
		["jsonrpc"] = "2.0",
		["method"] = "session/update",
		["params"] = new JsonObject { ["sessionId"] = session, ["update"] = new JsonObject { ["sessionUpdate"] = kind } },
	});
}
