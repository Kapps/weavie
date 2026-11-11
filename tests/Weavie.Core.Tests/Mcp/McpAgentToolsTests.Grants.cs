using System.Net;
using System.Net.WebSockets;
using Weavie.Core.Agents;
using Weavie.Core.Mcp;
using Xunit;

namespace Weavie.Core.Tests;

public sealed partial class McpAgentToolsTests {
	[Fact]
	public async Task GrantsBindHttpAndWebSocketAdmissionAndRefuseCallsAfterRevocation() {
		int firstAdmissions = 0;
		int siblingAdmissions = 0;
		await using var first = _server.CreateGrant(() => { Interlocked.Increment(ref firstAdmissions); return null; });
		await using var sibling = _server.CreateGrant(() => { Interlocked.Increment(ref siblingAdmissions); return null; });
		Assert.NotEqual(first.Credential.Token, sibling.Credential.Token);
		using var firstSocket = await ConnectAsync(first.Credential.Token);
		using var siblingSocket = await ConnectAsync(sibling.Credential.Token);
		using var client = new HttpClient();

		using (var reply = await CallAsync(firstSocket, 1, "listAgents", "{}")) Assert.Contains("codex", Text(reply), StringComparison.Ordinal);
		using (var reply = await CallAsync(siblingSocket, 1, "listAgents", "{}")) Assert.Contains("codex", Text(reply), StringComparison.Ordinal);
		string list = Request(1, "tools/call", """{"name":"listAgents","arguments":{}}""");
		foreach (var grant in new[] { first, sibling }) {
			using var response = await PostAsync(client, list, null, grant);
			using var reply = await TestMcp.ReadResponseAsync(response);
			Assert.Contains("codex", Text(reply), StringComparison.Ordinal);
		}
		Assert.Equal(2, firstAdmissions);
		Assert.Equal(2, siblingAdmissions);

		await first.DisposeAsync();

		using (var response = await PostAsync(client, list, null, first)) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
		await Assert.ThrowsAsync<WebSocketException>(() => ConnectAsync(first.Credential.Token));
		using (var refused = await CallAsync(firstSocket, 2, "listAgents", "{}")) {
			Assert.Contains("conversation has ended", refused.RootElement.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
		}
		using (var reply = await CallAsync(siblingSocket, 2, "listAgents", "{}")) Assert.Contains("codex", Text(reply), StringComparison.Ordinal);
		using (var response = await PostAsync(client, list, null, sibling)) {
			using var reply = await TestMcp.ReadResponseAsync(response);
			Assert.Contains("codex", Text(reply), StringComparison.Ordinal);
		}
		Assert.Equal(2, firstAdmissions);
		Assert.Equal(4, siblingAdmissions);
	}

	[Fact]
	public async Task RevokingOneGrantCancelsItsConsultButTheSiblingCanFinish() {
		await using var first = _server.CreateGrant(() => null);
		await using var sibling = _server.CreateGrant(() => null);
		var (firstStarted, siblingStarted, release) = BlockGrantConsults();
		using var firstSocket = await ConnectAsync(first.Credential.Token);
		using var siblingSocket = await ConnectAsync(sibling.Credential.Token);
		await SendAsync(firstSocket, GrantConsult("first"));
		await SendAsync(siblingSocket, GrantConsult("sibling"));
		var firstToken = await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		var siblingToken = await siblingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

		await first.DisposeAsync();

		await WaitCancelledAsync(firstToken);
		Assert.False(siblingToken.IsCancellationRequested);
		release.SetResult();
		using var reply = await ReceiveAsync(siblingSocket);
		Assert.Contains("sibling finished", Text(reply), StringComparison.Ordinal);
		Assert.False(siblingToken.IsCancellationRequested);
	}

	[Fact]
	public async Task HttpCancellationCannotCrossGrantsWithTheSameSessionAndRequestId() {
		await using var first = _server.CreateGrant(() => null);
		await using var sibling = _server.CreateGrant(() => null);
		var (firstStarted, siblingStarted, release) = BlockGrantConsults();
		using var client = new HttpClient();
		const string session = "shared-mcp-session";
		var firstCall = PostAsync(client, GrantConsult("first"), session, first);
		var firstToken = await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		var siblingCall = PostAsync(client, GrantConsult("sibling"), session, sibling);
		var siblingToken = await siblingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

		using (var acknowledgement = await PostAsync(client,
			"""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":7}}""", session, first)) {
			Assert.Equal(HttpStatusCode.Accepted, acknowledgement.StatusCode);
		}

		await WaitCancelledAsync(firstToken);
		Assert.False(siblingToken.IsCancellationRequested);
		using var cancelled = await firstCall.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Empty(await cancelled.Content.ReadAsStringAsync());
		release.SetResult();
		using var completed = await siblingCall.WaitAsync(TimeSpan.FromSeconds(5));
		using var reply = await TestMcp.ReadResponseAsync(completed);
		Assert.Contains("sibling finished", Text(reply), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RevocationSealsCompletionReceiptsWithoutWaitingForOtherTools() {
		await using var grant = _server.CreateGrant(() => null);
		var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_codex.Consult = async (request, ct) => {
			started.SetResult(ct);
			await release.Task;
			return new AgentConsultSuccess { ModelId = "astra", Controls = [], Reply = "released", Denied = [] };
		};
		using var socket = await ConnectAsync(grant.Credential.Token);
		await SendAsync(socket, GrantConsult("held"));
		var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		var receipt = grant.BeginCompletionResponse();
		try {
			await grant.DisposeAsync();
			Assert.True(token.IsCancellationRequested);
			Assert.Throws<ToolUnavailableException>(() => grant.BeginCompletionResponse());
			var drained = grant.CompletionResponses;
			Assert.False(drained.IsCompleted);
			grant.EndCompletionResponse(receipt);
			await drained.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.False(release.Task.IsCompleted);
		} finally {
			release.TrySetResult();
		}
	}

	private (TaskCompletionSource<CancellationToken> First, TaskCompletionSource<CancellationToken> Sibling, TaskCompletionSource Release) BlockGrantConsults() {
		var first = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
		var sibling = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_codex.Consult = async (request, ct) => {
			(request.Prompt == "first" ? first : sibling).SetResult(ct);
			await release.Task.WaitAsync(ct);
			return new AgentConsultSuccess { ModelId = "astra", Controls = [], Reply = $"{request.Prompt} finished", Denied = [] };
		};
		return (first, sibling, release);
	}

	private static string GrantConsult(string prompt) => Request(7, "tools/call",
		$$$"""{"name":"consultAgent","arguments":{"provider":"codex","prompt":"{{{prompt}}}"}}""");

	private async Task<HttpResponseMessage> PostAsync(HttpClient client, string json, string? session, CapabilityRegistryGrant grant) {
		using var request = Post(json, session, grant.Credential.Token);
		return await client.SendAsync(request).WaitAsync(TimeSpan.FromSeconds(5));
	}
}
