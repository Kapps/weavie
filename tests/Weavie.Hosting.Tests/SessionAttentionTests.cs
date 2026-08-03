using Weavie.Core.Hooks;
using Weavie.Core.Sessions;
using Weavie.Hosting.Messaging;
using Xunit;
using static Weavie.Hosting.Tests.TestHooks;

namespace Weavie.Hosting.Tests;

/// <summary>
/// Session attention over a real <see cref="HostCore"/>: a turn
/// settling (Working → Idle) publishes on the owning session bus; a permission
/// prompt pushes <c>needsInput</c>; a self-resuming stop (Waiting) and the trailing idle notice push nothing.
/// This asserts the exact JSON at the bridge seam — the same payload the WSS carries to the web client.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class SessionAttentionTests {
	[Fact]
	public async Task TurnComplete_PushesSessionAttention_WithSlotIdentity() {
		await using var host = await TestHost.StartAsync();
		var session = host.SelectedSession;

		session.Status.ObserveHook(Hook(HookEventKind.UserPromptSubmit));
		session.Status.ObserveHook(Stop(sessionWillResume: false));

		var attention = Assert.Single(
			host.Bridge.PostedEvents(session.Address, "attention", "raised"));
		Assert.Equal("turnComplete", attention.GetProperty("kind").GetString());
		Assert.Equal("Turn complete — waiting on you.", attention.GetProperty("body").GetString());
		Assert.False(string.IsNullOrEmpty(attention.GetProperty("label").GetString()));

		// The trailing "waiting for your input" notice fires right after Stop; it must not double-ping.
		session.Status.ObserveHook(Hook(HookEventKind.Notification, message: "Claude is waiting for your input"));
		Assert.Single(host.Bridge.PostedEvents(session.Address, "attention", "raised"));
	}

	[Fact]
	public async Task PermissionPrompt_PushesNeedsInput() {
		await using var host = await TestHost.StartAsync();
		var session = host.SelectedSession;

		session.Status.ObserveHook(Hook(HookEventKind.UserPromptSubmit));
		session.Status.ObserveHook(Hook(HookEventKind.Notification, message: "Claude needs your permission to use Bash"));

		var attention = Assert.Single(
			host.Bridge.PostedEvents(session.Address, "attention", "raised"));
		Assert.Equal("needsInput", attention.GetProperty("kind").GetString());
	}

	[Fact]
	public async Task SelfResumingStop_PushesNothing() {
		await using var host = await TestHost.StartAsync();
		var session = host.SelectedSession;

		session.Status.ObserveHook(Hook(HookEventKind.UserPromptSubmit));
		session.Status.ObserveHook(Stop(sessionWillResume: true));

		Assert.Empty(host.Bridge.PostedEvents(session.Address, "attention", "raised"));
	}

	[Fact]
	public async Task NativeNotification_UsesCanonicalContent_AndActivationTargetsExactOwner() {
		var notifications = new FakeSystemNotificationChannel {
			Permission = SystemNotificationPermission.NotDetermined,
			RequestedPermission = SystemNotificationPermission.Granted,
		};
		await using var host = await TestHost.StartAsync(notifications);
		var address = host.PrimarySession.Address;

		var initial = await host.HostRequestAsync<NotificationPermissionReply>(
			"notifications", "permission", new { });
		var requested = await host.HostRequestAsync<NotificationPermissionReply>(
			"notifications", "requestPermission", new { });
		Assert.Equal("notDetermined", initial.Permission);
		Assert.Equal("granted", requested.Permission);
		Assert.Equal(1, notifications.PermissionRequestCount);

		await host.HostRequestAsync<NotificationShownReply>(
			"notifications",
			"show",
			new {
				backendId = "runner-a",
				address,
				label = "Feature branch",
				kind = "needsInput",
			});
		var first = Assert.Single(notifications.Shown);
		Assert.Equal("Feature branch", first.Title);
		Assert.Equal("Needs your input.", first.Body);

		await host.HostRequestAsync<NotificationShownReply>(
			"notifications",
			"show",
			new {
				backendId = "runner-a",
				address,
				label = "Feature branch",
				kind = "turnComplete",
			});
		var second = notifications.Shown[1];
		Assert.Equal(first.ReplacementId, second.ReplacementId);
		Assert.NotEqual(first.ActivationId, second.ActivationId);

		notifications.Activate(first.ActivationId, "stale-token");
		Assert.Equal(0, host.Platform.ActivationCount);

		notifications.Activate(second.ActivationId, "wayland-token");
		Assert.Equal(1, host.Platform.ActivationCount);
		Assert.Equal("wayland-token", host.Platform.LastActivationToken);
		var activated = Assert.Single(host.Bridge.PostedEvents("notifications", "activated"));
		Assert.Equal("runner-a", activated.GetProperty("backendId").GetString());
		Assert.Equal(address.Slot, activated.GetProperty("address").GetProperty("slot").GetString());
		Assert.Equal(
			address.Incarnation,
			activated.GetProperty("address").GetProperty("incarnation").GetString());
		var (sentPeer, _) = Assert.Single(host.Bridge.Sent, entry =>
			MessageEnvelope.TryParse(entry.Json, out var envelope)
			&& envelope is { Kind: MessageKind.Event, Feature: "notifications", Name: "activated" });
		Assert.Equal(new WebPeer(TestHost.TestPageId), sentPeer);
	}

	[Fact]
	public async Task NativeNotification_ReplacingDeliveryKeepsOldActivationUntilCommit() {
		var notifications = new FakeSystemNotificationChannel {
			Permission = SystemNotificationPermission.Granted,
			RequestedPermission = SystemNotificationPermission.Granted,
		};
		await using var host = await TestHost.StartAsync(notifications);
		var address = host.PrimarySession.Address;
		await host.HostRequestAsync<NotificationShownReply>(
			"notifications",
			"show",
			new {
				backendId = "local",
				address,
				label = "Primary",
				kind = "turnComplete",
			});
		var previous = Assert.Single(notifications.Shown);
		notifications.BlockShow = true;

		host.EnqueueHostEvent(
			"notifications",
			"show",
			new {
				backendId = "local",
				address,
				label = "Primary",
				kind = "needsInput",
			});
		await Wait.UntilAsync(() => notifications.Shown.Count == 2);
		notifications.Activate(previous.ActivationId, "old-token");

		Assert.Equal(1, host.Platform.ActivationCount);
		Assert.Equal("old-token", host.Platform.LastActivationToken);
		var activated = Assert.Single(host.Bridge.PostedEvents("notifications", "activated"));
		Assert.Equal(address.Incarnation, activated.GetProperty("address").GetProperty("incarnation").GetString());

		notifications.CompleteShow();
	}

	[Fact]
	public async Task NativeNotification_IsRemovedWhenItsPageDisconnects() {
		var notifications = new FakeSystemNotificationChannel {
			Permission = SystemNotificationPermission.Granted,
			RequestedPermission = SystemNotificationPermission.Granted,
		};
		await using var host = await TestHost.StartAsync(notifications);
		await host.HostRequestAsync<NotificationShownReply>(
			"notifications",
			"show",
			new {
				backendId = "local",
				address = host.PrimarySession.Address,
				label = "Primary",
				kind = "failed",
			});

		host.Bridge.Disconnect(new WebPeer(TestHost.TestPageId));
		host.DrainMessages();
		await Wait.UntilAsync(() => notifications.Removed.Count == 1);
		Assert.Equal(Assert.Single(notifications.Shown).ReplacementId, Assert.Single(notifications.Removed));
	}

	[Fact]
	public async Task NativeNotification_ShutdownDrainsStartedDeliveryBeforeRemovingIt() {
		var notifications = new FakeSystemNotificationChannel {
			Permission = SystemNotificationPermission.Granted,
			RequestedPermission = SystemNotificationPermission.Granted,
			BlockShow = true,
		};
		await using var host = await TestHost.StartAsync(notifications);
		host.EnqueueHostEvent(
			"notifications",
			"show",
			new {
				backendId = "local",
				address = host.PrimarySession.Address,
				label = "Primary",
				kind = "turnComplete",
			});
		await notifications.ShowStarted.Task;

		var shutdown = host.Core.DisposeAsync().AsTask();

		Assert.False(shutdown.IsCompleted);
		Assert.Empty(notifications.Removed);
		notifications.CompleteShow();
		await shutdown;
		Assert.Equal(Assert.Single(notifications.Shown).ReplacementId, Assert.Single(notifications.Removed));
	}

	[Fact]
	public async Task NativeNotification_DisconnectDrainsStartedDeliveryBeforeRemovingIt() {
		var notifications = new FakeSystemNotificationChannel {
			Permission = SystemNotificationPermission.Granted,
			RequestedPermission = SystemNotificationPermission.Granted,
			BlockShow = true,
		};
		var dispatcher = new CountingUiDispatcher();
		await using var host = await TestHost.StartAsync(notifications, dispatcher);
		host.EnqueueHostEvent(
			"notifications",
			"show",
			new {
				backendId = "local",
				address = host.PrimarySession.Address,
				label = "Primary",
				kind = "needsInput",
			});
		await notifications.ShowStarted.Task;
		int postsBeforeDisconnect = dispatcher.PostCount;

		host.Bridge.Disconnect(new WebPeer(TestHost.TestPageId));
		await Wait.UntilAsync(() => dispatcher.PostCount >= postsBeforeDisconnect + 2);
		Assert.Empty(notifications.Removed);

		notifications.CompleteShow();
		await Wait.UntilAsync(() => notifications.Removed.Count == 1);
		notifications.Activate(Assert.Single(notifications.Shown).ActivationId, null);
		Assert.Equal(0, host.Platform.ActivationCount);
	}

	[Fact]
	public async Task NativeNotification_DisconnectAttemptsEveryRemoval() {
		var notifications = new FakeSystemNotificationChannel {
			Permission = SystemNotificationPermission.Granted,
			RequestedPermission = SystemNotificationPermission.Granted,
		};
		await using var host = await TestHost.StartAsync(notifications);
		await host.HostRequestAsync<NotificationShownReply>(
			"notifications",
			"show",
			new {
				backendId = "local",
				address = host.PrimarySession.Address,
				label = "Primary",
				kind = "failed",
			});
		await host.HostRequestAsync<NotificationShownReply>(
			"notifications",
			"show",
			new {
				backendId = "local",
				address = new SessionAddress("secondary", "secondary-incarnation"),
				label = "Secondary",
				kind = "failed",
			});
		notifications.FailedRemovals.Add(notifications.Shown[0].ReplacementId);

		host.Bridge.Disconnect(new WebPeer(TestHost.TestPageId));
		host.DrainMessages();

		await Wait.UntilAsync(() => notifications.Removed.Count == 2);
		Assert.Equal(
			notifications.Shown.Select(notification => notification.ReplacementId).Order(),
			notifications.Removed.Order());
	}

	private sealed record NotificationPermissionReply(string Permission);

	private sealed record NotificationShownReply(bool Shown);

	private sealed class FakeSystemNotificationChannel : ISystemNotificationChannel {
		public required SystemNotificationPermission Permission { get; set; }
		public required SystemNotificationPermission RequestedPermission { get; init; }
		public List<SystemNotification> Shown { get; } = [];
		public List<string> Removed { get; } = [];
		public HashSet<string> FailedRemovals { get; } = new(StringComparer.Ordinal);
		public bool BlockShow { get; set; }
		public TaskCompletionSource ShowStarted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public int PermissionRequestCount { get; private set; }
		private readonly TaskCompletionSource _showCompletion =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public event Action<SystemNotificationActivation>? Activated;

		public Task<SystemNotificationPermission> GetPermissionAsync(CancellationToken ct) {
			ct.ThrowIfCancellationRequested();
			return Task.FromResult(Permission);
		}

		public Task<SystemNotificationPermission> RequestPermissionAsync(CancellationToken ct) {
			ct.ThrowIfCancellationRequested();
			PermissionRequestCount++;
			Permission = RequestedPermission;
			return Task.FromResult(Permission);
		}

		public Task ShowAsync(SystemNotification notification, CancellationToken ct) {
			ct.ThrowIfCancellationRequested();
			Shown.Add(notification);
			ShowStarted.TrySetResult();
			return BlockShow ? _showCompletion.Task : Task.CompletedTask;
		}

		public Task RemoveAsync(string replacementId, CancellationToken ct) {
			ct.ThrowIfCancellationRequested();
			Removed.Add(replacementId);
			if (FailedRemovals.Contains(replacementId)) {
				throw new InvalidOperationException($"Could not remove {replacementId}.");
			}
			return Task.CompletedTask;
		}

		public void CompleteShow() => _showCompletion.TrySetResult();

		public void Activate(string activationId, string? activationToken) =>
			Activated?.Invoke(new SystemNotificationActivation(activationId, activationToken));
	}

	private sealed class CountingUiDispatcher : IUiDispatcher {
		private int _postCount;

		public int PostCount => Volatile.Read(ref _postCount);

		public void Post(Action action) {
			ArgumentNullException.ThrowIfNull(action);
			Interlocked.Increment(ref _postCount);
			action();
		}
	}
}
