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

	[Fact]
	public async Task Delete_WithoutId_TargetsTheOwningSession() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("feature")).Ok);
		Assert.Contains("feature", SessionIds(host));

		var result = await host.InvokeCommandAsync(
			"feature",
			SessionCommands.DeleteSession,
			new { },
			CancellationToken.None);

		Assert.True(result.Ok, result.Error);
		Assert.DoesNotContain("feature", SessionIds(host));
	}

	[Fact]
	public async Task SelectedSessionRepliesBeforeDeletingItsOwnMessageBus() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("feature")).Ok);

		var result = await host.InvokeClientCommandAsync(
			SessionCommands.DeleteSession,
			new { });

		Assert.True(result.Ok, result.Error);
		host.SelectSession("primary");
		await Wait.ForAsync<bool>(() => SessionIds(host).Contains("feature") ? null : true);
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

		var result = await host.InvokeCommandAsync(
			"feature",
			SessionCommands.UnloadSession,
			new { },
			CancellationToken.None);

		Assert.True(result.Ok, result.Error);
		var feature = host.Bridge.LastEvent("sessions", "catalog")!.Value
			.EnumerateArray().Single(s => s.GetProperty("id").GetString() == "feature");
		Assert.False(feature.GetProperty("loaded").GetBoolean());
	}

	[Fact]
	public async Task ConcurrentUnloadFromDifferentSessionsTearsTheTargetDownOnce() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("feature")).Ok);

		var fromPrimary = host.InvokeCommandAsync(
			"primary",
			SessionCommands.UnloadSession,
			new { id = "feature" },
			CancellationToken.None);
		var fromOwner = host.InvokeCommandAsync(
			"feature",
			SessionCommands.UnloadSession,
			new { },
			CancellationToken.None);
		var results = await Task.WhenAll(fromPrimary, fromOwner);

		Assert.All(results, result => Assert.True(result.Ok, result.Error));
		var feature = host.Bridge.LastEvent("sessions", "catalog")!.Value
			.EnumerateArray().Single(s => s.GetProperty("id").GetString() == "feature");
		Assert.False(feature.GetProperty("loaded").GetBoolean());
		Assert.Null(host.Core.SessionForTest("feature"));
	}

	[Fact]
	public async Task UnloadStopsWhenTheAttachedEditorCannotFlush() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("feature")).Ok);
		var originalResponder = host.Bridge.RequestResponder;
		host.Bridge.RequestResponder = request =>
			request is { Feature: "editor", Name: "flush" }
				? new FakeWebResponse(JsonSerializer.SerializeToElement<object?>(null), "disk full")
				: originalResponder?.Invoke(request);

		var result = await host.InvokeCommandAsync(
			"primary",
			SessionCommands.UnloadSession,
			new { id = "feature" },
			CancellationToken.None);

		Assert.False(result.Ok);
		Assert.Contains("disk full", result.Error);
		Assert.Same(host.Session("feature"), host.Core.SessionForTest("feature"));
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

		var result = await host.DeleteSessionAsync("feature", force: false, classify: false);

		Assert.True(result.Ok, result.Error);
		Assert.DoesNotContain("feature", SessionIds(host));
	}
}
