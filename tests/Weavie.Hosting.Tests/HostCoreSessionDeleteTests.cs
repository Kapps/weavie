using System.Text.Json;
using Weavie.Core.Commands;
using Weavie.Hosting.Messaging;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// Session lifecycle commands default to their owning session, never a focused session on some page. Explicit
/// ids still target another known slot. Runs against a real <see cref="HostCore"/> over a temp git repo.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreSessionDeleteTests {
	private static IReadOnlyList<string?> SessionIds(TestHost host) {
		var list = host.Bridge.LastEvent("sessions", "catalog");
		return list is null ? [] : [.. list.Value.EnumerateArray().Select(s => s.GetProperty("id").GetString())];
	}

	private static string[] NotificationMessages(TestHost host) => [.. host.Bridge
		.PostedEvents("notifications", "show")
		.Select(notification => notification.GetProperty("message").GetString()!)];

	[Fact]
	public async Task Delete_WithoutId_TargetsTheOwningSession() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("feature")).Ok);
		Assert.Contains("feature", SessionIds(host));
		host.Bridge.Clear();

		var result = await host.InvokeCommandAsync(
			"feature",
			SessionCommands.DeleteSession,
			new { },
			CancellationToken.None);

		Assert.True(result.Ok, result.Error);
		Assert.Null(result.Message);
		Assert.DoesNotContain("feature", SessionIds(host));
		Assert.Equal(
			new[] { "Session 'feature' was deleted. Its branch was kept." },
			NotificationMessages(host));
	}

	[Fact]
	public async Task SelectedSessionRepliesBeforeDeletingItsOwnMessageBus() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("feature")).Ok);
		host.Bridge.Clear();

		var result = await host.InvokeClientCommandAsync(
			SessionCommands.DeleteSession,
			new { });

		Assert.True(result.Ok, result.Error);
		Assert.Null(result.Message);
		host.SelectWorkspaceSession();
		await Wait.ForAsync<bool>(() => SessionIds(host).Contains("feature") ? null : true);
		Assert.Equal(
			new[] { "Session 'feature' was deleted. Its branch was kept." },
			NotificationMessages(host));
		var posted = host.Bridge.Posted
			.Select(json => MessageEnvelope.TryParse(json, out var envelope) ? envelope : null)
			.ToArray();
		int responseIndex = Array.FindIndex(
			posted,
			envelope => envelope is { Kind: MessageKind.Response, Feature: "commands", Name: "invoke" });
		int toastIndex = Array.FindIndex(
			posted,
			envelope => envelope is { Kind: MessageKind.Event, Feature: "notifications", Name: "show" });
		Assert.True(responseIndex >= 0);
		Assert.True(toastIndex > responseIndex);
	}

	[Fact]
	public async Task ShutdownCancelsSelfDeleteWaitingToEnterTheUiLane() {
		var dispatcher = new ManualUiDispatcher(paused: false);
		await using var host = TestHost.CreateUnstarted(dispatcher);
		await host.Core.StartAsync();
		await host.ConnectAsync();
		Assert.True((await host.CreateSessionAsync("feature")).Ok);
		var session = host.Session("feature");
		string worktree = session.WorkspaceRoot;
		const string requestId = "self-delete-pending";
		dispatcher.Pause();
		host.Bridge.Receive(
			new WebPeer(TestHost.TestPageId),
			MessageEnvelope.SessionRequest(
				session.Address,
				requestId,
				"commands",
				"invoke",
				JsonSerializer.SerializeToElement(new {
					id = SessionCommands.DeleteSession,
					args = JsonSerializer.SerializeToElement(new { force = true }),
				})).ToJson());
		await dispatcher.WaitForPostAsync().WaitAsync(TimeSpan.FromSeconds(2));
		Assert.True(dispatcher.RunNext());
		await dispatcher.WaitForPostAsync().WaitAsync(TimeSpan.FromSeconds(2));
		Assert.True(dispatcher.RunNext());

		var response = await Wait.ForReferenceAsync(() => host.Bridge.Posted
			.Select(json => MessageEnvelope.TryParse(json, out var envelope) ? envelope : null)
			.LastOrDefault(envelope => envelope is { Kind: MessageKind.Response, RequestId: requestId }));
		Assert.Null(response.Error);
		Assert.True(response.Payload.GetProperty("ok").GetBoolean());
		await dispatcher.WaitForPostAsync().WaitAsync(TimeSpan.FromSeconds(2));

		var dispose = Task.Run(() => host.Core.DisposeAsync().AsTask().GetAwaiter().GetResult());
		try {
			await dispose.WaitAsync(TimeSpan.FromSeconds(2));
		} finally {
			dispatcher.RunPending();
		}

		Assert.True(Directory.Exists(worktree));
	}

	[Fact]
	public async Task Unload_WithoutId_ParksTheOwningSession() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("feature")).Ok);
		host.Bridge.Clear();

		var result = await host.InvokeCommandAsync(
			"feature",
			SessionCommands.UnloadSession,
			new { },
			CancellationToken.None);

		Assert.True(result.Ok, result.Error);
		Assert.Null(result.Message);
		var feature = host.Bridge.LastEvent("sessions", "catalog")!.Value
			.EnumerateArray().Single(s => s.GetProperty("id").GetString() == "feature");
		Assert.False(feature.GetProperty("loaded").GetBoolean());
		Assert.Equal(
			new[] { "Session 'feature' was unloaded. Its worktree was kept." },
			NotificationMessages(host));
	}

	[Fact]
	public async Task ConcurrentUnloadFromDifferentSessionsTearsTheTargetDownOnce() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("feature")).Ok);
		host.Bridge.Clear();

		var fromWorkspace = host.InvokeCommandAsync(
			host.WorkspaceSession.SlotId,
			SessionCommands.UnloadSession,
			new { id = "feature" },
			CancellationToken.None);
		var fromOwner = host.InvokeCommandAsync(
			"feature",
			SessionCommands.UnloadSession,
			new { },
			CancellationToken.None);
		var results = await Task.WhenAll(fromWorkspace, fromOwner);

		Assert.All(results, result => Assert.True(result.Ok, result.Error));
		var feature = host.Bridge.LastEvent("sessions", "catalog")!.Value
			.EnumerateArray().Single(s => s.GetProperty("id").GetString() == "feature");
		Assert.False(feature.GetProperty("loaded").GetBoolean());
		Assert.Null(host.Core.SessionForTest("feature"));
		Assert.Equal(
			new[] { "Session 'feature' was unloaded. Its worktree was kept." },
			NotificationMessages(host));
	}

	[Fact]
	public async Task UnloadStopsWhenTheAttachedEditorCannotFlush() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("feature")).Ok);
		host.Bridge.Clear();
		var originalResponder = host.Bridge.RequestResponder;
		host.Bridge.RequestResponder = request =>
			request is { Feature: "editor", Name: "flush" }
				? new FakeWebResponse(JsonSerializer.SerializeToElement<object?>(null), "disk full")
				: originalResponder?.Invoke(request);

		var result = await host.InvokeCommandAsync(
			host.WorkspaceSession.SlotId,
			SessionCommands.UnloadSession,
			new { id = "feature" },
			CancellationToken.None);

		Assert.False(result.Ok);
		Assert.Contains("disk full", result.Error);
		Assert.Same(host.Session("feature"), host.Core.SessionForTest("feature"));
		Assert.Empty(NotificationMessages(host));
	}

	[Fact]
	public async Task Classify_WithoutId_UsesTheOwningSession() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("feature")).Ok);

		var result = await host.InvokeCommandAsync(
			"feature",
			SessionCommands.DeleteSession,
			new { classify = true },
			CancellationToken.None);

		Assert.True(result.Ok, result.Error);
	}

	[Fact]
	public async Task Delete_UnknownId_ReportsNoSuchSession() {
		await using var host = await TestHost.StartAsync();

		var result = await host.DeleteSessionAsync("no-such-branch", force: false, classify: false);

		Assert.False(result.Ok);
		Assert.Contains("No such session", result.Error!);
	}

	[Fact]
	public async Task Delete_ByExplicitId_RemovesThatSessionFromTheRail() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("feature")).Ok);
		Assert.Contains("feature", SessionIds(host));
		host.SelectWorkspaceSession();
		host.Bridge.Clear();

		var result = await host.DeleteSessionAsync("feature", force: false, classify: false);

		Assert.True(result.Ok, result.Error);
		Assert.Null(result.Message);
		Assert.DoesNotContain("feature", SessionIds(host));
		Assert.Equal(
			new[] { "Session 'feature' was deleted. Its branch was kept." },
			NotificationMessages(host));
	}

	[Fact]
	public async Task WorkspaceSessionCanBeUnloadedThenDeletedWithoutTouchingTheCheckout() {
		await using var host = await TestHost.StartAsync();
		string id = host.WorkspaceSession.SlotId;

		var result = await host.InvokeCommandAsync(
			id,
			SessionCommands.UnloadSession,
			new { },
			CancellationToken.None);

		Assert.True(result.Ok, result.Error);
		var slot = host.Bridge.LastEvent("sessions", "catalog")!.Value
			.EnumerateArray().Single(entry => entry.GetProperty("id").GetString() == id);
		Assert.False(slot.GetProperty("loaded").GetBoolean());
		Assert.True(Directory.Exists(host.RepoRoot));

		var deleted = await host.HostRequestAsync<JsonElement>(
			"sessions",
			"invoke",
			new { id = SessionCommands.DeleteSession, args = new { id } });
		Assert.True(deleted.GetProperty("ok").GetBoolean());
		var replacement = Assert.Single(host.Bridge.LastEvent("sessions", "catalog")!.Value.EnumerateArray());
		Assert.NotEqual(id, replacement.GetProperty("id").GetString());
		Assert.Contains(NotificationMessages(host), message => message.Contains("was deleted", StringComparison.Ordinal));
		Assert.True(Directory.Exists(host.RepoRoot));
	}

	[Fact]
	public async Task HostSessionCommandsWorkWithNoLiveSessionRuntime() {
		await using var host = await TestHost.StartAsync();
		string id = host.WorkspaceSession.SlotId;

		var unloaded = await host.HostRequestAsync<JsonElement>(
			"sessions",
			"invoke",
			new { id = SessionCommands.UnloadSession, args = new { id } });

		Assert.True(unloaded.GetProperty("ok").GetBoolean());
		Assert.Null(host.Core.SessionForTest(id));
		var clientCommand = await host.HostRequestAsync<JsonElement>(
			"commands",
			"invoke",
			new { id = CoreCommands.IncreaseFontSize, args = new { } });
		Assert.True(clientCommand.GetProperty("ok").GetBoolean());

		var loaded = await host.HostRequestAsync<JsonElement>(
			"sessions",
			"invoke",
			new { id = SessionCommands.LoadSession, args = new { id } });

		Assert.True(loaded.GetProperty("ok").GetBoolean());
		Assert.NotNull(host.Core.SessionForTest(id));
	}

	[Fact]
	public async Task DeletingWorkspaceSessionKeepsTheCheckoutAndOtherSessions() {
		await using var host = await TestHost.StartAsync();
		string workspaceId = host.WorkspaceSession.SlotId;
		Assert.True((await host.CreateSessionAsync("feature")).Ok);
		var classification = await host.DeleteSessionAsync(workspaceId, force: false, classify: true);
		using var data = JsonDocument.Parse(classification.DataJson!);
		Assert.False(data.RootElement.GetProperty("removesCheckout").GetBoolean());

		var result = await host.DeleteSessionAsync(workspaceId, force: false, classify: false);

		Assert.True(result.Ok, result.Error);
		Assert.DoesNotContain(workspaceId, SessionIds(host));
		Assert.Contains("feature", SessionIds(host));
		Assert.True(Directory.Exists(host.RepoRoot));
	}

	[Fact]
	public async Task DeletedWorkspaceSessionDoesNotReturnOnRestartWhenAnotherSessionExists() {
		await using var host = await TestHost.StartAsync();
		string workspaceId = host.WorkspaceSession.SlotId;
		Assert.True((await host.CreateSessionAsync("feature")).Ok);
		Assert.True((await host.DeleteSessionAsync(workspaceId, force: false, classify: false)).Ok);

		await host.RestartAsync();

		Assert.DoesNotContain(workspaceId, SessionIds(host));
		Assert.Equal(["feature"], SessionIds(host));
		Assert.Equal("feature", host.SelectedSession.SlotId);
		Assert.True(Directory.Exists(host.RepoRoot));
	}

	[Fact]
	public async Task DeletingTheLastSessionCreatesAFreshWorkspaceSession() {
		await using var host = await TestHost.StartAsync();
		string deletedId = host.WorkspaceSession.SlotId;

		var result = await host.InvokeCommandAsync(
			deletedId,
			SessionCommands.DeleteSession,
			new { },
			CancellationToken.None);

		Assert.True(result.Ok, result.Error);
		var replacement = Assert.Single(host.Bridge.LastEvent("sessions", "catalog")!.Value.EnumerateArray());
		Assert.NotEqual(deletedId, replacement.GetProperty("id").GetString());
		Assert.Equal("main", replacement.GetProperty("label").GetString());
		Assert.True(replacement.GetProperty("loaded").GetBoolean());
		Assert.True(Directory.Exists(host.RepoRoot));
	}
}
