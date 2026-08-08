using Weavie.Core.Sessions;

namespace Weavie.Win.Hosting;

/// <summary>The process-wide Windows Shell notification-area router.</summary>
internal sealed class WindowsNotificationService : IDisposable {
	private readonly WindowsBalloonNotifications _balloons;
	private readonly SystemNotificationRoutes<SystemNotificationChannel> _routes = new();
	private readonly object _gate = new();
	private bool _disposed;

	public WindowsNotificationService() {
		_balloons = new WindowsBalloonNotifications();
		_balloons.Activated += OnBalloonActivated;
		_balloons.Closed += OnBalloonClosed;
	}

	/// <summary>Creates one workspace-owned channel on the shared manager.</summary>
	public SystemNotificationChannel CreateChannel() {
		lock (_gate) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			return new SystemNotificationChannel(
				PermissionAsync,
				PermissionAsync,
				ShowAsync,
				RemoveAsync,
				DisposeChannel);
		}
	}

	private static Task<SystemNotificationPermission> PermissionAsync(CancellationToken ct) {
		ct.ThrowIfCancellationRequested();
		return Task.FromResult(SystemNotificationPermission.Granted);
	}

	private Task ShowAsync(
		SystemNotificationChannel channel,
		SystemNotification notification,
		CancellationToken ct) {
		ct.ThrowIfCancellationRequested();
		SystemNotificationRouteRegistration registration;
		lock (_gate) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			registration = _routes.Replace(channel, notification);
		}
		try {
			_balloons.Show(notification);
			registration.Commit();
		} catch {
			registration.Rollback();
			throw;
		}
		return Task.CompletedTask;
	}

	private Task RemoveAsync(
		SystemNotificationChannel channel,
		string replacementId,
		CancellationToken ct) {
		ct.ThrowIfCancellationRequested();
		Forget(channel, replacementId);
		_balloons.Remove(replacementId);
		return Task.CompletedTask;
	}

	private void DisposeChannel(SystemNotificationChannel channel) {
		string[] replacementIds = _routes.ForgetOwner(channel);
		foreach (string replacementId in replacementIds) {
			_balloons.Remove(replacementId);
		}
	}

	private void Forget(SystemNotificationChannel channel, string replacementId) =>
		_routes.Forget(channel, replacementId);

	private void OnBalloonActivated(SystemNotification notification) {
		if (_routes.TryTake(notification.ActivationId, out var owner)) {
			owner.Activate(notification.ActivationId, null);
		}
	}

	private void OnBalloonClosed(SystemNotification notification) =>
		_routes.TryTake(notification.ActivationId, out _);

	/// <inheritdoc/>
	public void Dispose() {
		lock (_gate) {
			if (_disposed) {
				return;
			}
			_disposed = true;
			_routes.Clear();
		}
		_balloons.Activated -= OnBalloonActivated;
		_balloons.Closed -= OnBalloonClosed;
		_balloons.Dispose();
	}
}
