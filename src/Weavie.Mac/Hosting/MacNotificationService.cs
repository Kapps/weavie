using Foundation;
using UserNotifications;
using Weavie.Core.Sessions;

namespace Weavie.Mac.Hosting;

/// <summary>The process-wide macOS UserNotifications delegate and workspace-channel router.</summary>
internal sealed class MacNotificationService : UNUserNotificationCenterDelegate, IDisposable {
	private static readonly NSString ActivationKey = new("weavieActivation");
	private readonly UNUserNotificationCenter _center = UNUserNotificationCenter.Current;
	private readonly SystemNotificationRoutes<SystemNotificationChannel> _routes = new();
	private readonly object _gate = new();
	private bool _disposed;

	public MacNotificationService() {
		_center.Delegate = this;
	}

	/// <summary>Creates one workspace-owned channel on the shared notification center.</summary>
	public SystemNotificationChannel CreateChannel() {
		lock (_gate) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			return new SystemNotificationChannel(
				GetPermissionAsync,
				RequestPermissionAsync,
				ShowAsync,
				RemoveAsync,
				DisposeChannel);
		}
	}

	internal async Task<SystemNotificationPermission> GetPermissionAsync(CancellationToken ct) {
		var completion = new TaskCompletionSource<SystemNotificationPermission>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var registration = ct.Register(() => completion.TrySetCanceled(ct));
		_center.GetNotificationSettings(settings => completion.TrySetResult(Permission(settings.AuthorizationStatus)));
		return await completion.Task.ConfigureAwait(false);
	}

	internal async Task<SystemNotificationPermission> RequestPermissionAsync(CancellationToken ct) {
		var completion = new TaskCompletionSource<SystemNotificationPermission>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var registration = ct.Register(() => completion.TrySetCanceled(ct));
		_center.RequestAuthorization(UNAuthorizationOptions.Alert, (granted, error) => {
			if (error is not null) {
				completion.TrySetException(new NSErrorException(error));
			} else {
				completion.TrySetResult(granted
					? SystemNotificationPermission.Granted
					: SystemNotificationPermission.Denied);
			}
		});
		return await completion.Task.ConfigureAwait(false);
	}

	internal async Task ShowAsync(
		SystemNotificationChannel channel,
		SystemNotification notification,
		CancellationToken ct) {
		if (await GetPermissionAsync(ct).ConfigureAwait(false) != SystemNotificationPermission.Granted) {
			throw new InvalidOperationException("macOS notifications are disabled for Weavie.");
		}

		var content = new UNMutableNotificationContent {
			Title = notification.Title,
			Body = notification.Body,
			Sound = null,
			UserInfo = new NSDictionary<NSString, NSObject>(ActivationKey, new NSString(notification.ActivationId)),
		};
		var request = UNNotificationRequest.FromIdentifier(notification.ReplacementId, content, null);
		ct.ThrowIfCancellationRequested();
		SystemNotificationRouteRegistration registration;
		lock (_gate) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			registration = _routes.Replace(channel, notification);
		}

		var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_center.AddNotificationRequest(request, error => {
			if (error is null) {
				completion.TrySetResult();
			} else {
				completion.TrySetException(new NSErrorException(error));
			}
		});
		try {
			await completion.Task.ConfigureAwait(false);
			registration.Commit();
		} catch {
			registration.Rollback();
			throw;
		}
	}

	internal Task RemoveAsync(SystemNotificationChannel channel, string replacementId, CancellationToken ct) {
		ct.ThrowIfCancellationRequested();
		Forget(channel, replacementId);
		_center.RemovePendingNotificationRequests([replacementId]);
		_center.RemoveDeliveredNotifications([replacementId]);
		return Task.CompletedTask;
	}

	internal void DisposeChannel(SystemNotificationChannel channel) {
		string[] replacementIds = _routes.ForgetOwner(channel);
		if (replacementIds.Length > 0) {
			_center.RemovePendingNotificationRequests(replacementIds);
			_center.RemoveDeliveredNotifications(replacementIds);
		}
	}

	/// <inheritdoc/>
	public override void WillPresentNotification(
		UNUserNotificationCenter center,
		UNNotification notification,
		Action<UNNotificationPresentationOptions> completionHandler) =>
		completionHandler(UNNotificationPresentationOptions.Banner | UNNotificationPresentationOptions.List);

	/// <inheritdoc/>
	public override void DidReceiveNotificationResponse(
		UNUserNotificationCenter center,
		UNNotificationResponse response,
		Action completionHandler) {
		try {
			if (response.Notification.Request.Content.UserInfo[ActivationKey] is NSString activation) {
				string activationId = activation.ToString();
				if (_routes.TryTake(activationId, out var owner)) {
					owner.Activate(activationId, null);
				}
			}
		} finally {
			completionHandler();
		}
	}

	private void Forget(SystemNotificationChannel channel, string replacementId) =>
		_routes.Forget(channel, replacementId);

	private static SystemNotificationPermission Permission(UNAuthorizationStatus status) => status switch {
		UNAuthorizationStatus.NotDetermined => SystemNotificationPermission.NotDetermined,
		UNAuthorizationStatus.Authorized or UNAuthorizationStatus.Provisional => SystemNotificationPermission.Granted,
		UNAuthorizationStatus.Denied => SystemNotificationPermission.Denied,
		_ => throw new ArgumentOutOfRangeException(nameof(status), status, "unhandled notification authorization status"),
	};

	/// <inheritdoc/>
	public new void Dispose() {
		lock (_gate) {
			if (_disposed) {
				return;
			}
			_disposed = true;
			_routes.Clear();
		}
		_center.Delegate = null;
		base.Dispose();
	}
}
