using Weavie.Core.Sessions;

namespace Weavie.Win.Hosting;

/// <summary>The process-wide Windows Shell notification-area router.</summary>
internal sealed class WindowsNotificationService : IDisposable {
	private readonly WindowsBalloonNotifications _balloons;
	private readonly SystemNotificationRoutes<WindowsNotificationChannel> _routes = new();
	private readonly object _gate = new();
	private bool _disposed;

	public WindowsNotificationService() {
		_balloons = new WindowsBalloonNotifications();
		_balloons.Activated += OnBalloonActivated;
		_balloons.Closed += OnBalloonClosed;
	}

	/// <summary>Creates one workspace-owned channel on the shared manager.</summary>
	public WindowsNotificationChannel CreateChannel() {
		lock (_gate) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			return new WindowsNotificationChannel(this);
		}
	}

	internal static SystemNotificationPermission Permission() => SystemNotificationPermission.Granted;

	internal void Show(WindowsNotificationChannel channel, SystemNotification notification) {
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
	}

	internal Task RemoveAsync(
		WindowsNotificationChannel channel,
		string replacementId,
		CancellationToken ct) {
		ct.ThrowIfCancellationRequested();
		Forget(channel, replacementId);
		_balloons.Remove(replacementId);
		return Task.CompletedTask;
	}

	internal void DisposeChannel(WindowsNotificationChannel channel) {
		string[] replacementIds = _routes.ForgetOwner(channel);
		foreach (string replacementId in replacementIds) {
			_balloons.Remove(replacementId);
		}
	}

	private void Forget(WindowsNotificationChannel channel, string replacementId) =>
		_routes.Forget(channel, replacementId);

	private void OnBalloonActivated(SystemNotification notification) {
		if (_routes.TryTake(notification.ActivationId, out var owner)) {
			owner.Activate(notification.ActivationId);
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

/// <summary>One Windows workspace window's notification identities and activation event.</summary>
internal sealed class WindowsNotificationChannel : ISystemNotificationChannel, IDisposable {
	private readonly WindowsNotificationService _service;
	private bool _disposed;

	internal WindowsNotificationChannel(WindowsNotificationService service) {
		_service = service;
	}

	/// <inheritdoc/>
	public event Action<SystemNotificationActivation>? Activated;

	/// <inheritdoc/>
	public Task<SystemNotificationPermission> GetPermissionAsync(CancellationToken ct) {
		ct.ThrowIfCancellationRequested();
		return Task.FromResult(WindowsNotificationService.Permission());
	}

	/// <inheritdoc/>
	public Task<SystemNotificationPermission> RequestPermissionAsync(CancellationToken ct) =>
		GetPermissionAsync(ct);

	/// <inheritdoc/>
	public Task ShowAsync(SystemNotification notification, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(notification);
		ct.ThrowIfCancellationRequested();
		ObjectDisposedException.ThrowIf(_disposed, this);
		_service.Show(this, notification);
		return Task.CompletedTask;
	}

	/// <inheritdoc/>
	public Task RemoveAsync(string replacementId, CancellationToken ct) {
		ArgumentException.ThrowIfNullOrEmpty(replacementId);
		ObjectDisposedException.ThrowIf(_disposed, this);
		return _service.RemoveAsync(this, replacementId, ct);
	}

	internal void Activate(string activationId) {
		if (!_disposed) {
			Activated?.Invoke(new SystemNotificationActivation(activationId, null));
		}
	}

	/// <inheritdoc/>
	public void Dispose() {
		if (_disposed) {
			return;
		}
		_disposed = true;
		_service.DisposeChannel(this);
	}
}
