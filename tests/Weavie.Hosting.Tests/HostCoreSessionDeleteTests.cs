using System.Text.Json;
using Weavie.Core;
using Weavie.Core.Commands;
using Weavie.Core.Workspaces;
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

	private static MessageEnvelope? PostedEnvelope(TestHost host, Func<MessageEnvelope, bool> match) =>
		host.Bridge.Posted
			.Select(json => MessageEnvelope.TryParse(json, out var envelope) ? envelope : null)
			.LastOrDefault(envelope => envelope is not null && match(envelope));

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
	public async Task DeleteWaitingForEditorFlushDoesNotBlockAnotherSessionCommand() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("feature")).Ok);
		var source = host.WorkspaceSession;
		var originalResponder = host.Bridge.RequestResponder;
		MessageEnvelope? flush = null;
		host.Bridge.RequestResponder = request =>
			request is { Feature: "editor", Name: "flush" }
				? null
				: originalResponder?.Invoke(request);

		try {
			host.Bridge.Receive(
				new WebPeer(TestHost.TestPageId),
				MessageEnvelope.SessionRequest(
					source.Address,
					"slow-delete",
					"commands",
					"invoke",
					JsonSerializer.SerializeToElement(new {
						id = SessionCommands.DeleteSession,
						args = new { id = "feature" },
					})).ToJson());
			flush = await Wait.ForReferenceAsync(() => PostedEnvelope(
				host,
				envelope => envelope is { Kind: MessageKind.Request, Feature: "editor", Name: "flush" }));

			host.Bridge.Receive(
				new WebPeer(TestHost.TestPageId),
				MessageEnvelope.SessionRequest(
					source.Address,
					"view-logs",
					"commands",
					"invoke",
					JsonSerializer.SerializeToElement(new {
						id = CoreCommands.ViewLogs,
						args = new { },
					})).ToJson());

			var response = await Wait.ForReferenceAsync(() => PostedEnvelope(
				host,
				envelope => envelope is { Kind: MessageKind.Response, RequestId: "view-logs" }));
			Assert.Null(response.Error);
			Assert.True(response.Payload.GetProperty("ok").GetBoolean());
			Assert.Null(PostedEnvelope(
				host,
				envelope => envelope is { Kind: MessageKind.Response, RequestId: "slow-delete" }));
		} finally {
			host.Bridge.RequestResponder = originalResponder;
			if (flush is not null) {
				host.Bridge.Receive(
					new WebPeer(TestHost.TestPageId),
					MessageEnvelope.Response(
						flush.Scope,
						flush.Session,
						flush.RequestId!,
						flush.Feature,
						flush.Name,
						JsonSerializer.SerializeToElement<object?>(null),
						"test released the flush").ToJson());
			}
		}

		var deleteResponse = await Wait.ForReferenceAsync(() => PostedEnvelope(
			host,
			envelope => envelope is { Kind: MessageKind.Response, RequestId: "slow-delete" }));
		Assert.False(deleteResponse.Payload.GetProperty("ok").GetBoolean());
	}

	[Fact]
	public async Task HostSessionCommandRoutingContinuesWhileDeleteWaitsForEditorFlush() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("feature")).Ok);
		var originalResponder = host.Bridge.RequestResponder;
		MessageEnvelope? flush = null;
		host.Bridge.RequestResponder = request =>
			request is { Feature: "editor", Name: "flush" }
				? null
				: originalResponder?.Invoke(request);

		try {
			host.Bridge.Receive(
				new WebPeer(TestHost.TestPageId),
				MessageEnvelope.Request(
					MessageScope.Host,
					null,
					"slow-host-delete",
					"sessions",
					"invoke",
					JsonSerializer.SerializeToElement(new {
						id = SessionCommands.DeleteSession,
						args = new { id = "feature" },
					})).ToJson());
			flush = await Wait.ForReferenceAsync(() => PostedEnvelope(
				host,
				envelope => envelope is { Kind: MessageKind.Request, Feature: "editor", Name: "flush" }));

			host.Bridge.Receive(
				new WebPeer(TestHost.TestPageId),
				MessageEnvelope.Request(
					MessageScope.Host,
					null,
					"fast-host-route",
					"sessions",
					"invoke",
					JsonSerializer.SerializeToElement(new {
						id = CoreCommands.ViewLogs,
						args = new { },
					})).ToJson());

			var response = await Wait.ForReferenceAsync(() => PostedEnvelope(
				host,
				envelope => envelope is { Kind: MessageKind.Response, RequestId: "fast-host-route" }));
			Assert.Null(response.Error);
			Assert.False(response.Payload.GetProperty("ok").GetBoolean());
			Assert.Contains("not a host-scoped session command", response.Payload.GetProperty("error").GetString());

			host.Bridge.Receive(
				new WebPeer(TestHost.TestPageId),
				MessageEnvelope.Request(
					MessageScope.Host,
					null,
					"queued-classify",
					"sessions",
					"invoke",
					JsonSerializer.SerializeToElement(new {
						id = SessionCommands.DeleteSession,
						args = new { id = "feature", classify = true },
					})).ToJson());
			MessageHealthSnapshot? health = null;
			for (int attempt = 0; attempt < 80; attempt++) {
				health = await host.Core.MessageHealthAsync(CancellationToken.None);
				if (health.ActiveOperations.Any(operation => operation.RequestId == "queued-classify")
					|| PostedEnvelope(
						host,
						envelope => envelope is { Kind: MessageKind.Response, RequestId: "queued-classify" }) is not null) {
					break;
				}

				await Task.Delay(25);
			}
			Assert.Null(PostedEnvelope(
				host,
				envelope => envelope is { Kind: MessageKind.Response, RequestId: "queued-classify" }));
			Assert.Contains(health!.ActiveOperations, operation => operation.RequestId == "queued-classify");
		} finally {
			host.Bridge.RequestResponder = originalResponder;
			if (flush is not null) {
				host.Bridge.Receive(
					new WebPeer(TestHost.TestPageId),
					MessageEnvelope.Response(
						flush.Scope,
						flush.Session,
						flush.RequestId!,
						flush.Feature,
						flush.Name,
						JsonSerializer.SerializeToElement<object?>(null),
						"test released the flush").ToJson());
			}
		}

		await Wait.ForReferenceAsync(() => PostedEnvelope(
			host,
			envelope => envelope is { Kind: MessageKind.Response, RequestId: "slow-host-delete" }));
		var classifyResponse = await Wait.ForReferenceAsync(() => PostedEnvelope(
			host,
			envelope => envelope is { Kind: MessageKind.Response, RequestId: "queued-classify" }));
		Assert.True(classifyResponse.Payload.GetProperty("ok").GetBoolean());
	}

	[Fact]
	public async Task NewSessionRejectsASourceDeletedWhileWaitingForTheLifecycleLane() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("feature")).Ok);
		host.SelectWorkspaceSession();
		var source = host.WorkspaceSession.Address;
		var originalResponder = host.Bridge.RequestResponder;
		MessageEnvelope? flush = null;
		host.Bridge.RequestResponder = request =>
			request is { Feature: "editor", Name: "flush" }
				? null
				: originalResponder?.Invoke(request);

		try {
			host.Bridge.Receive(
				new WebPeer(TestHost.TestPageId),
				MessageEnvelope.Request(
					MessageScope.Host,
					null,
					"stale-source-delete",
					"sessions",
					"invoke",
					JsonSerializer.SerializeToElement(new {
						id = SessionCommands.DeleteSession,
						args = new { id = source.Slot, force = true },
					})).ToJson());
			flush = await Wait.ForReferenceAsync(() => PostedEnvelope(
				host,
				envelope => envelope is { Kind: MessageKind.Request, Feature: "editor", Name: "flush" }));

			host.Bridge.Receive(
				new WebPeer(TestHost.TestPageId),
				MessageEnvelope.Request(
					MessageScope.Host,
					null,
					"stale-source-create",
					"sessions",
					"invoke",
					JsonSerializer.SerializeToElement(new {
						id = SessionCommands.NewSession,
						args = new {
							branch = "after-delete",
							@base = "source",
							existing = false,
							source = new { slot = source.Slot, incarnation = source.Incarnation },
						},
					})).ToJson());

			var flushResponse = originalResponder?.Invoke(flush);
			Assert.NotNull(flushResponse);
			host.Bridge.Receive(
				new WebPeer(TestHost.TestPageId),
				MessageEnvelope.Response(
					flush.Scope,
					flush.Session,
					flush.RequestId!,
					flush.Feature,
					flush.Name,
					flushResponse.Payload,
					flushResponse.Error).ToJson());
			flush = null;

			var deleteResponse = await Wait.ForReferenceAsync(() => PostedEnvelope(
				host,
				envelope => envelope is { Kind: MessageKind.Response, RequestId: "stale-source-delete" }));
			Assert.True(deleteResponse.Payload.GetProperty("ok").GetBoolean());
			var createResponse = await Wait.ForReferenceAsync(() => PostedEnvelope(
				host,
				envelope => envelope is { Kind: MessageKind.Response, RequestId: "stale-source-create" }));
			Assert.False(createResponse.Payload.GetProperty("ok").GetBoolean());
			Assert.Contains(
				"source session no longer exists",
				createResponse.Payload.GetProperty("error").GetString());
		} finally {
			host.Bridge.RequestResponder = originalResponder;
			if (flush is not null) {
				host.Bridge.Receive(
					new WebPeer(TestHost.TestPageId),
					MessageEnvelope.Response(
						flush.Scope,
						flush.Session,
						flush.RequestId!,
						flush.Feature,
						flush.Name,
						JsonSerializer.SerializeToElement<object?>(null),
						"test released the flush").ToJson());
			}
		}
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

	// Puts a checkout Weavie did not create on the rail: git creates it, reconcile discovers it at the next open.
	private static async Task<string> DiscoverCheckoutAsync(TestHost host, Action<string> afterAdd) {
		string checkout = Path.Combine(Path.GetDirectoryName(host.RepoRoot)!, "manual");
		await host.RestartAsync(() => {
			TestHost.RunGit(host.RepoRoot, "worktree", "add", checkout, "-b", "manual");
			afterAdd(checkout);
		});
		return checkout;
	}

	// A checkout Weavie did not create — another workspace's worktree, an agent's, a hand-made one — is
	// discovered onto the rail, so deleting it has to remove it; otherwise git keeps reporting it and the next
	// open rediscovers the session forever.
	[Fact]
	public async Task DeletingADiscoveredCheckoutRemovesItsWorktreeForGood() {
		await using var host = await TestHost.StartAsync();
		string checkout = await DiscoverCheckoutAsync(host, static _ => { });
		Assert.Contains("manual", SessionIds(host));

		var result = await host.DeleteSessionAsync("manual", force: false, classify: false);

		Assert.True(result.Ok, result.Error);
		Assert.False(Directory.Exists(checkout));
		await host.RestartAsync();
		Assert.DoesNotContain("manual", SessionIds(host));
	}

	[Fact]
	public async Task DeletingADiscoveredCheckoutWithChangesNeedsForce() {
		await using var host = await TestHost.StartAsync();
		string checkout = await DiscoverCheckoutAsync(
			host,
			static tree => File.WriteAllText(Path.Combine(tree, "readme.txt"), "edited\n"));

		var blocked = await host.DeleteSessionAsync("manual", force: false, classify: false);

		Assert.False(blocked.Ok);
		Assert.Contains("uncommitted changes", blocked.Error);
		Assert.True(Directory.Exists(checkout));
		Assert.Contains("manual", SessionIds(host));

		Assert.True((await host.DeleteSessionAsync("manual", force: true, classify: false)).Ok);
		Assert.False(Directory.Exists(checkout));
	}

	[Fact]
	public async Task ClassifyingADiscoveredCheckoutReportsTheRemovalAndItsState() {
		await using var host = await TestHost.StartAsync();
		await DiscoverCheckoutAsync(
			host,
			static tree => File.WriteAllText(Path.Combine(tree, "readme.txt"), "edited\n"));

		var classification = await host.DeleteSessionAsync("manual", force: false, classify: true);

		using var data = JsonDocument.Parse(classification.DataJson!);
		Assert.True(data.RootElement.GetProperty("removesCheckout").GetBoolean());
		Assert.Equal("modified", data.RootElement.GetProperty("state").GetString());
		Assert.False(data.RootElement.GetProperty("branchless").GetBoolean());
	}

	// git dropping its record is no proof of a branch either: the delete must not hand-delete the directory as
	// though committed work were safe.
	[Fact]
	public async Task DeletingACheckoutGitNoLongerReportsNeedsForce() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("feature")).Ok);
		string checkout = host.Session("feature").WorkspaceRoot;
		Directory.Delete(
			Directory.GetDirectories(Path.Combine(host.RepoRoot, ".git", "worktrees")).Single(),
			recursive: true);

		var blocked = await host.DeleteSessionAsync("feature", force: false, classify: false);

		Assert.False(blocked.Ok);
		Assert.Contains("no branch keeping its commits", blocked.Error);
		Assert.True(Directory.Exists(checkout));

		Assert.True((await host.DeleteSessionAsync("feature", force: true, classify: false)).Ok);
		Assert.False(Directory.Exists(checkout));
	}

	// Git refuses to remove a locked worktree even with force, so the delete fails in git's own words rather
	// than dropping the session and leaving a checkout the next open rediscovers.
	[Fact]
	public async Task DeletingADiscoveredCheckoutIsRefusedWhenItsWorktreeIsLocked() {
		await using var host = await TestHost.StartAsync();
		string checkout = await DiscoverCheckoutAsync(host, static _ => { });
		TestHost.RunGit(host.RepoRoot, "worktree", "lock", checkout);

		var result = await host.DeleteSessionAsync("manual", force: true, classify: false);

		Assert.False(result.Ok);
		Assert.Contains("locked working tree", result.Error);
		Assert.True(Directory.Exists(checkout));
		Assert.Contains("manual", SessionIds(host));
	}

	// A detached checkout has no branch to keep its commits, so removing it orphans them: that costs force, and
	// the toast must not claim a branch survived.
	[Fact]
	public async Task DeletingABranchlessCheckoutNeedsForceAndSaysNoBranchSurvived() {
		await using var host = await TestHost.StartAsync();
		string checkout = await DiscoverCheckoutAsync(
			host,
			static tree => TestHost.RunGit(tree, "checkout", "--detach"));
		var classification = await host.DeleteSessionAsync("manual", force: false, classify: true);
		using (var data = JsonDocument.Parse(classification.DataJson!)) {
			Assert.True(data.RootElement.GetProperty("branchless").GetBoolean());
		}

		var blocked = await host.DeleteSessionAsync("manual", force: false, classify: false);

		Assert.False(blocked.Ok);
		Assert.Contains("no branch keeping its commits", blocked.Error);
		Assert.True(Directory.Exists(checkout));

		host.Bridge.Clear();
		Assert.True((await host.DeleteSessionAsync("manual", force: true, classify: false)).Ok);
		Assert.False(Directory.Exists(checkout));
		Assert.Contains(
			"Session 'manual' was deleted. Its checkout had no branch to keep.",
			NotificationMessages(host));
	}

	[Fact]
	public async Task RegistryLossDoesNotPreventDeletingTheOwnedCheckout() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("feature")).Ok);
		string checkout = host.Session("feature").WorkspaceRoot;
		string registry = WeaviePaths.WorkspaceWorktreesFile(WorkspaceId.ForPath(host.RepoRoot));

		await host.RestartAsync(() => File.Delete(registry));
		var classification = await host.DeleteSessionAsync("feature", force: false, classify: true);
		using (var data = JsonDocument.Parse(classification.DataJson!)) {
			Assert.True(data.RootElement.GetProperty("removesCheckout").GetBoolean());
		}

		var result = await host.DeleteSessionAsync("feature", force: false, classify: false);

		Assert.True(result.Ok, result.Error);
		Assert.False(Directory.Exists(checkout));
		await host.RestartAsync();
		Assert.DoesNotContain("feature", SessionIds(host));
	}

	[Fact]
	public async Task RegistryLossDoesNotPreventDeletingADetachedOwnedCheckout() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("detached")).Ok);
		string checkout = host.Session("detached").WorkspaceRoot;
		TestHost.RunGit(checkout, "checkout", "--detach");
		string registry = WeaviePaths.WorkspaceWorktreesFile(WorkspaceId.ForPath(host.RepoRoot));

		await host.RestartAsync(() => File.Delete(registry));
		var classification = await host.DeleteSessionAsync("detached", force: false, classify: true);
		using (var data = JsonDocument.Parse(classification.DataJson!)) {
			Assert.True(data.RootElement.GetProperty("removesCheckout").GetBoolean());
		}

		var blocked = await host.DeleteSessionAsync("detached", force: false, classify: false);
		Assert.False(blocked.Ok);
		var result = await host.DeleteSessionAsync("detached", force: true, classify: false);

		Assert.True(result.Ok, result.Error);
		Assert.False(Directory.Exists(checkout));
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
