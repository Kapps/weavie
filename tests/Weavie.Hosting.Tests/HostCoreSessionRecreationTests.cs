using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Xunit;

namespace Weavie.Hosting.Tests;

[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreSessionRecreationTests {
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RecreatePreservesCheckoutAndEditorStateAcrossRestart(bool worktree) {
		await using var host = await TestHost.StartAsync();
		if (worktree) Assert.True((await host.CreateSessionAsync("feature")).Ok);
		var original = worktree ? host.Session("feature") : host.WorkspaceSession;
		host.SelectSession(original.SlotId);
		string path = Path.Combine(original.WorkspaceRoot, "readme.txt");
		await File.WriteAllTextAsync(path, "Keep these edits");
		host.SessionEvent(original, "editor", "sessionChanged", new {
			session = new { active = path, open = new[] { new { path } } },
		});

		var result = await RecreateAsync(host, original.SlotId, "structured");
		Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());
		var replacement = host.Session(original.SlotId);
		Assert.NotEqual(original.Incarnation, replacement.Incarnation);
		Assert.Equal(original.WorkspaceRoot, replacement.WorkspaceRoot);
		Assert.Equal("structured", replacement.Agent.Provider.Id);
		Assert.Equal(path, replacement.EditorSession.Active);
		Assert.Equal("Keep these edits", await File.ReadAllTextAsync(path));

		await host.RestartAsync();
		Assert.Equal("structured", host.Session(original.SlotId).Agent.Provider.Id);
		Assert.Equal(path, host.Session(original.SlotId).EditorSession.Active);
	}

	[Fact]
	public async Task RecreatingTerminalAlwaysAllocatesAFreshConversation() {
		await using var host = await TestHost.StartAsync();
		var original = host.WorkspaceSession;
		var launch = original.Agent.TerminalSession!.ResolveLaunch();
		string originalId = ConversationId(launch);
		Assert.True((await RecreateAsync(host, original.SlotId, "structured")).GetProperty("ok").GetBoolean());
		Assert.True((await RecreateAsync(host, original.SlotId, "claude")).GetProperty("ok").GetBoolean());
		string returnedId = ConversationId(host.WorkspaceSession.Agent.TerminalSession!.ResolveLaunch());
		Assert.NotEqual(originalId, returnedId);
		Assert.True((await RecreateAsync(host, original.SlotId, "claude")).GetProperty("ok").GetBoolean());
		Assert.NotEqual(returnedId, ConversationId(host.WorkspaceSession.Agent.TerminalSession!.ResolveLaunch()));
	}

	[Fact]
	public async Task DormantSessionCanBeRecreatedWithAnotherProvider() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("feature")).Ok);
		Assert.True((await host.UnloadSessionAsync("feature")).Ok);
		Assert.True((await RecreateAsync(host, "feature", "structured")).GetProperty("ok").GetBoolean());
		Assert.Equal("structured", host.Session("feature").Agent.Provider.Id);
	}

	[Theory]
	[InlineData("")]
	[InlineData("missing-provider")]
	public async Task InvalidProviderLeavesRuntimeIntact(string provider) {
		await using var host = await TestHost.StartAsync();
		var original = host.WorkspaceSession;
		var result = await RecreateAsync(host, original.SlotId, provider);
		Assert.False(result.GetProperty("ok").GetBoolean());
		Assert.Same(original, host.WorkspaceSession);
	}

	[Fact]
	public async Task FailedEditorFlushLeavesRuntimeIntact() {
		await using var host = await TestHost.StartAsync();
		var original = host.WorkspaceSession;
		var responder = host.Bridge.RequestResponder;
		host.Bridge.RequestResponder = request => request is { Feature: "editor", Name: "flush" }
			? new FakeWebResponse(JsonSerializer.SerializeToElement<object?>(null), "disk full")
			: responder?.Invoke(request);
		var result = await RecreateAsync(host, original.SlotId, "structured");
		Assert.False(result.GetProperty("ok").GetBoolean());
		Assert.Contains("disk full", result.GetProperty("error").GetString());
		Assert.Same(original, host.WorkspaceSession);
	}

	[Fact]
	public async Task MissingExplicitIdDoesNotRecreateSelectedSession() {
		await using var host = await TestHost.StartAsync();
		var original = host.WorkspaceSession;
		var result = await host.InvokeClientCommandAsync(SessionCommands.RecreateSession, new { agentProviderId = "structured" });
		Assert.False(result.Ok);
		Assert.Same(original, host.WorkspaceSession);
	}

	[Fact]
	public async Task SelfRecreateRepliesBeforeRetiringItsEndpoint() {
		await using var host = await TestHost.StartAsync();
		var original = host.WorkspaceSession;
		var result = await host.InvokeClientCommandAsync(SessionCommands.RecreateSession,
			new { id = original.SlotId, agentProviderId = "structured" });
		Assert.True(result.Ok, result.Error);
		await Wait.ForAsync<bool>(() => host.WorkspaceSession.Incarnation != original.Incarnation ? true : null);
		Assert.Equal("structured", host.WorkspaceSession.Agent.Provider.Id);
	}

	private static Task<JsonElement> RecreateAsync(TestHost host, string id, string agentProviderId) =>
		host.HostRequestAsync<JsonElement>("sessions", "invoke", new {
			id = SessionCommands.RecreateSession,
			args = new { id, agentProviderId },
		});

	private static string ConversationId(AgentLaunch launch) {
		var args = launch.Arguments.ToList();
		int index = args.FindIndex(arg => arg is "--session-id" or "--resume");
		Assert.True(index >= 0, string.Join(' ', args));
		return args[index + 1];
	}
}
