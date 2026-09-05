using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Weavie.AgentClientProtocol;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpSessionEndpointTests {
	[Fact]
	public async Task EndpointRejectsCallerSuppliedSessionIdentity() {
		await using var connection = Connection(Directory.GetCurrentDirectory());
		var endpoint = connection.OpenEndpoint(1, "primary");
		var forged = new { sessionId = "another-conversation" };

		await Assert.ThrowsAsync<ArgumentException>(() => endpoint.RequestAsync("session/prompt", forged, CancellationToken.None));
		await Assert.ThrowsAsync<ArgumentException>(() => endpoint.NotifyAsync("session/cancel", forged));
		await Assert.ThrowsAsync<ArgumentException>(() => endpoint.ForkAsync(forged, CancellationToken.None));
		await Assert.ThrowsAsync<ArgumentException>(() => endpoint.CreateAsync(forged, CancellationToken.None));
	}

	[Fact]
	public async Task OpeningEndpointQueuesUpdatesInOrderAndRetirementIsLocal() {
		await using var connection = Connection(Directory.GetCurrentDirectory());
		var primary = connection.OpenEndpoint(1, "primary");
		var opening = connection.OpenEndpoint(1);
		List<string> primaryUpdates = [];
		List<string> sideUpdates = [];
		List<Exception> errors = [];
		primary.Attach((_, update) => primaryUpdates.Add(update.GetString()!), _ => Assert.Fail("Unexpected request"), errors.Add);
		opening.Notify(JsonSerializer.SerializeToElement("early-one"));
		opening.Bind("side");
		opening.Notify(JsonSerializer.SerializeToElement("early-two"));
		opening.Attach((_, update) => sideUpdates.Add(update.GetString()!), _ => Assert.Fail("Unexpected request"), errors.Add);
		opening.Retire();
		opening.Notify(JsonSerializer.SerializeToElement("late-side"));
		primary.Notify(JsonSerializer.SerializeToElement("primary-still-live"));

		Assert.Equal(["early-one", "early-two"], sideUpdates);
		Assert.Equal(["primary-still-live"], primaryUpdates);
		Assert.Empty(errors);
		await Assert.ThrowsAsync<ObjectDisposedException>(() => opening.NotifyAsync("session/cancel", new { }));
		await Assert.ThrowsAsync<ObjectDisposedException>(() => opening.AuthenticateAsync("fake-login", CancellationToken.None));
		await Assert.ThrowsAsync<ObjectDisposedException>(() => opening.CreateAsync(new { }, CancellationToken.None));
	}

	[Fact]
	public async Task OverlappingForksWaitForTheFirstIdentityAndPreserveEarlyUpdates() {
		using var directory = new TempDirectory("weavie-acp-held-fork");
		string workspace = directory.CreateDirectory("workspace");
		await using var connection = Connection(workspace, "held-fork");
		var started = new TaskCompletionSource<AcpProcessGeneration>(TaskCreationOptions.RunContinuationsAsynchronously);
		connection.ProcessStarted += value => started.TrySetResult(value);
		connection.Start();
		long generation = (await started.Task.WaitAsync(TimeSpan.FromSeconds(10))).Generation;
		await InitializeAsync(connection, generation);
		var primary = connection.OpenEndpoint(generation);
		await primary.CreateAsync(OpenParameters(workspace), CancellationToken.None);
		using var cancellation = new CancellationTokenSource();
		var first = primary.ForkAsync(OpenParameters(workspace), cancellation.Token);
		await Wait.UntilAsync(() => File.Exists(Path.Combine(workspace, "fork-started")));
		cancellation.Cancel();
		var second = primary.ForkAsync(OpenParameters(workspace), CancellationToken.None);
		Assert.False(first.IsCompleted);
		Assert.False(second.IsCompleted);
		File.WriteAllText(Path.Combine(workspace, "release-fork"), string.Empty);
		var firstChild = await first.WaitAsync(TimeSpan.FromSeconds(10));
		var secondChild = await second.WaitAsync(TimeSpan.FromSeconds(10));
		List<JsonElement> firstUpdates = [];
		List<JsonElement> secondUpdates = [];
		List<Exception> errors = [];
		firstChild.Attach((_, value) => firstUpdates.Add(value), _ => Assert.Fail("Unexpected request"), errors.Add);
		secondChild.Attach((_, value) => secondUpdates.Add(value), _ => Assert.Fail("Unexpected request"), errors.Add);

		Assert.Equal(File.ReadAllText(Path.Combine(workspace, "fork-started")), firstChild.SessionId);
		Assert.NotEqual(firstChild.SessionId, secondChild.SessionId);
		Assert.Contains("early fork update", Assert.Single(firstUpdates).GetRawText(), StringComparison.Ordinal);
		Assert.Empty(secondUpdates);
		Assert.Empty(errors);
		Assert.Equal(2, File.ReadAllLines(Path.Combine(workspace, "fake-acp-state", "forks.log")).Length);
	}

	[Fact]
	public async Task EndpointIdentityIsUniqueWithinItsGenerationAndCannotBeRebound() {
		await using var connection = Connection(Directory.GetCurrentDirectory());
		var endpoint = connection.OpenEndpoint(1, "conversation");
		Assert.Throws<AcpProtocolException>(() => connection.OpenEndpoint(1, "conversation"));
		Assert.Throws<AcpProtocolException>(() => endpoint.Bind("changed"));
		var replacement = connection.OpenEndpoint(2, "conversation");
		Assert.Equal(1, endpoint.Generation);
		Assert.Equal(2, replacement.Generation);
		Assert.Equal(endpoint.SessionId, replacement.SessionId);
	}

	[Fact]
	public async Task ForkLoadBuffersReplayAndRoutesPromptsToTheirOwnerAcrossRestart() {
		using var directory = new TempDirectory("weavie-acp-endpoint");
		string workspace = directory.CreateDirectory("workspace");
		await using var connection = Connection(workspace);
		var generations = Channel.CreateUnbounded<AcpProcessGeneration>();
		connection.ProcessStarted += value => generations.Writer.TryWrite(value);
		connection.Start();
		long generation = (await generations.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10))).Generation;
		await InitializeAsync(connection, generation);
		var primary = connection.OpenEndpoint(generation);
		var primaryUpdates = new ConcurrentQueue<JsonElement>();
		var sideUpdates = new ConcurrentQueue<JsonElement>();
		var errors = new ConcurrentQueue<Exception>();
		primary.Attach((_, value) => primaryUpdates.Enqueue(value), _ => Assert.Fail("Unexpected request"), errors.Enqueue);
		await primary.CreateAsync(OpenParameters(workspace), CancellationToken.None);
		await PromptAsync(primary, "persisted primary context");
		var child = await primary.ForkAsync(OpenParameters(workspace), CancellationToken.None);
		await child.RequestAsync("session/load", OpenParameters(workspace), CancellationToken.None);
		child.Attach((_, value) => sideUpdates.Enqueue(value), _ => Assert.Fail("Unexpected request"), errors.Enqueue);
		Assert.Contains(sideUpdates, value => value.GetRawText().Contains("persisted primary context", StringComparison.Ordinal));
		await PromptAsync(child, "identify-session");
		await child.CloseAsync();
		await PromptAsync(primary, "identify-session");

		Assert.All(primaryUpdates, value => Assert.Equal(primary.SessionId, value.GetProperty("params").GetProperty("sessionId").GetString()));
		Assert.All(sideUpdates, value => Assert.Equal(child.SessionId, value.GetProperty("params").GetProperty("sessionId").GetString()));
		Assert.Contains(primaryUpdates, value => value.GetRawText().Contains("session: " + primary.SessionId, StringComparison.Ordinal));
		Assert.Contains(sideUpdates, value => value.GetRawText().Contains("session: " + child.SessionId, StringComparison.Ordinal));
		Assert.Empty(errors);

		connection.Restart();
		long replacement = (await generations.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10))).Generation;
		await Assert.ThrowsAnyAsync<InvalidOperationException>(() => PromptAsync(primary, "must not reach replacement"));
		await InitializeAsync(connection, replacement);
		Assert.DoesNotContain(File.ReadAllLines(Path.Combine(workspace, "fake-acp-state", "prompts.log")),
			value => value.Contains("must not reach replacement", StringComparison.Ordinal));
	}

	private static Task<JsonElement> PromptAsync(AcpSessionEndpoint endpoint, string text) =>
		endpoint.RequestAsync("session/prompt", new { prompt = new[] { new { type = "text", text } } }, CancellationToken.None);

	private static Task<JsonElement> InitializeAsync(AcpJsonRpcConnection connection, long generation) =>
		connection.RequestAsync("initialize", new { protocolVersion = 1, clientCapabilities = new { plan = new { } } }, generation, CancellationToken.None);

	private static object OpenParameters(string cwd) => new {
		cwd,
		mcpServers = new[] { new {
			name = "weavie", type = "http", url = "http://localhost/mcp",
			headers = new[] { new { name = "Authorization", value = "Bearer test" } },
		} },
	};

	private static AcpJsonRpcConnection Connection(string workspace) => Connection(workspace, string.Empty);

	private static AcpJsonRpcConnection Connection(string workspace, string mode) => new(new AcpAgentDefinition {
		Id = "endpoint",
		Name = "Endpoint fake",
		Command = AcpAgentSessionFixture.ExecutablePath("tools", "Weavie.FakeAcp", "weavie-fake-acp"),
		Arguments = [],
		Environment = new Dictionary<string, string>(StringComparer.Ordinal) {
			["WEAVIE_ROOT"] = workspace,
			["WEAVIE_FAKE_ACP_MODE"] = mode,
		},
		Distribution = "custom",
	}, workspace, _ => { });
}
