using Weavie.Core.Sessions;
using Weavie.Linux.Hosting;
using Xunit;

namespace Weavie.Linux.Tests;

public sealed class LinuxNotificationServiceTests {
	[Fact]
	public async Task Permission_ReflectsNotificationDaemonAvailability() {
		var transport = new FakeTransport { Available = false };
		using var service = new LinuxNotificationService(transport, _ => { });
		using var channel = service.CreateChannel();

		var permission = await channel.GetPermissionAsync(CancellationToken.None);

		Assert.Equal(SystemNotificationPermission.Unavailable, permission);
	}

	[Fact]
	public async Task Show_SendsDesktopIdentitySilentActionAndContent() {
		var transport = new FakeTransport { NextId = 41 };
		using var service = new LinuxNotificationService(transport, _ => { });
		using var channel = service.CreateChannel();

		await channel.ShowAsync(Notification("replacement", "activation", "Session A"), CancellationToken.None);

		var sent = Assert.Single(transport.Shown);
		Assert.Equal(0u, sent.ReplacesId);
		Assert.Equal("Weavie", sent.AppName);
		Assert.Equal(LinuxDesktopIdentity.AppId, sent.AppIcon);
		Assert.Equal(LinuxDesktopIdentity.AppId, sent.DesktopEntry);
		Assert.Equal("Session A", sent.Title);
		Assert.Equal("Needs your input", sent.Body);
		Assert.Equal(["default", "Open Weavie"], sent.Actions);
		Assert.True(sent.SuppressSound);
		Assert.Equal(-1, sent.ExpireTimeout);
	}

	[Fact]
	public async Task Replacement_UsesCurrentServerIdAndOnlyCurrentActivation() {
		var transport = new FakeTransport { NextId = 10 };
		using var service = new LinuxNotificationService(transport, _ => { });
		using var channel = service.CreateChannel();
		var activations = new List<SystemNotificationActivation>();
		channel.Activated += activations.Add;
		await channel.ShowAsync(Notification("slot", "first", "Session"), CancellationToken.None);
		transport.NextId = 11;

		await channel.ShowAsync(Notification("slot", "second", "Session"), CancellationToken.None);
		transport.RaiseActivation(10, "default", "stale-token");
		transport.RaiseActivation(11, "default", "current-token");

		Assert.Equal(10u, transport.Shown[1].ReplacesId);
		var activation = Assert.Single(activations);
		Assert.Equal("second", activation.Id);
		Assert.Equal("current-token", activation.ActivationToken);
	}

	[Fact]
	public async Task Replacement_KeepsNewRouteWhenDaemonReusesItsServerId() {
		var transport = new FakeTransport { NextId = 10 };
		using var service = new LinuxNotificationService(transport, _ => { });
		using var channel = service.CreateChannel();
		var activations = new List<SystemNotificationActivation>();
		channel.Activated += activations.Add;
		await channel.ShowAsync(Notification("slot", "first", "Session"), CancellationToken.None);

		await channel.ShowAsync(Notification("slot", "second", "Session"), CancellationToken.None);
		transport.RaiseActivation(10, "default", "token");

		Assert.Equal(10u, transport.Shown[1].ReplacesId);
		Assert.Equal("second", Assert.Single(activations).Id);
	}

	[Fact]
	public async Task ClosedNotification_CannotActivateAStaleRoute() {
		var transport = new FakeTransport { NextId = 8 };
		using var service = new LinuxNotificationService(transport, _ => { });
		using var channel = service.CreateChannel();
		var activations = new List<SystemNotificationActivation>();
		channel.Activated += activations.Add;
		await channel.ShowAsync(Notification("slot", "activation", "Session"), CancellationToken.None);

		transport.RaiseClosed(8);
		transport.RaiseActivation(8, "default", "token");

		Assert.Empty(activations);
	}

	[Fact]
	public async Task Remove_ClosesTheCurrentServerNotification() {
		var transport = new FakeTransport { NextId = 23 };
		using var service = new LinuxNotificationService(transport, _ => { });
		using var channel = service.CreateChannel();
		await channel.ShowAsync(Notification("slot", "activation", "Session"), CancellationToken.None);

		await channel.RemoveAsync("slot", CancellationToken.None);

		Assert.Equal([23u], transport.ClosedIds);
	}

	[Fact]
	public async Task Invalidation_ClearsReplacementAndActivationState() {
		var transport = new FakeTransport { NextId = 14 };
		using var service = new LinuxNotificationService(transport, _ => { });
		using var channel = service.CreateChannel();
		var activations = new List<SystemNotificationActivation>();
		channel.Activated += activations.Add;
		await channel.ShowAsync(Notification("slot", "before", "Session"), CancellationToken.None);
		transport.RaiseInvalidated();
		transport.RaiseActivation(14, "default", "token");
		transport.NextId = 15;

		await channel.ShowAsync(Notification("slot", "after", "Session"), CancellationToken.None);

		Assert.Empty(activations);
		Assert.Equal(0u, transport.Shown[1].ReplacesId);
	}

	[Fact]
	public async Task DisposedChannel_CannotReceiveAStaleActivation() {
		var transport = new FakeTransport { NextId = 29 };
		using var service = new LinuxNotificationService(transport, _ => { });
		var channel = service.CreateChannel();
		var activations = new List<SystemNotificationActivation>();
		channel.Activated += activations.Add;
		await channel.ShowAsync(Notification("slot", "activation", "Session"), CancellationToken.None);

		channel.Dispose();
		transport.RaiseActivation(29, "default", "token");

		Assert.Empty(activations);
	}

	[Fact]
	public async Task DeliveryFailure_PropagatesToTheCaller() {
		var transport = new FakeTransport {
			ShowError = new InvalidOperationException("daemon rejected notification"),
		};
		using var service = new LinuxNotificationService(transport, _ => { });
		using var channel = service.CreateChannel();

		var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			channel.ShowAsync(Notification("slot", "activation", "Session"), CancellationToken.None));

		Assert.Contains("daemon rejected", error.Message, StringComparison.Ordinal);
	}

	private static SystemNotification Notification(string replacement, string activation, string title) =>
		new(replacement, activation, title, "Needs your input");

	private sealed class FakeTransport : ILinuxNotificationTransport {
		public event Action<LinuxNotificationActivation>? Activated;
		public event Action<uint>? Closed;
		public event Action? Invalidated;

		internal bool Available { get; init; } = true;
		internal uint NextId { get; set; } = 1;
		internal Exception? ShowError { get; init; }
		internal List<LinuxNotificationRequest> Shown { get; } = [];
		internal List<uint> ClosedIds { get; } = [];

		public Task<bool> IsAvailableAsync(CancellationToken ct) {
			ct.ThrowIfCancellationRequested();
			return Task.FromResult(Available);
		}

		public Task<uint> ShowAsync(LinuxNotificationRequest notification, CancellationToken ct) {
			ct.ThrowIfCancellationRequested();
			Shown.Add(notification);
			return ShowError is null
				? Task.FromResult(NextId)
				: Task.FromException<uint>(ShowError);
		}

		public Task CloseAsync(uint id, CancellationToken ct) {
			ct.ThrowIfCancellationRequested();
			ClosedIds.Add(id);
			return Task.CompletedTask;
		}

		internal void RaiseActivation(uint id, string action, string? token) =>
			Activated?.Invoke(new LinuxNotificationActivation(id, action, token));

		internal void RaiseClosed(uint id) => Closed?.Invoke(id);

		internal void RaiseInvalidated() => Invalidated?.Invoke();

		public void Dispose() {
		}
	}
}
