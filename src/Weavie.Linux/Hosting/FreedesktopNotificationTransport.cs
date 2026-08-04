using Tmds.DBus.Protocol;
using DesktopNotificationProxy = Weavie.Linux.DesktopNotifications.Notifications;

namespace Weavie.Linux.Hosting;

internal sealed class FreedesktopNotificationTransport : ILinuxNotificationTransport {
	private const string Destination = "org.freedesktop.Notifications";
	private const string Path = "/org/freedesktop/Notifications";
	private readonly string? _address = DBusAddress.Session;
	private readonly SemaphoreSlim _connectionGate = new(1, 1);
	private readonly object _gate = new();
	private readonly Dictionary<uint, string> _activationTokens = [];
	private NotificationConnection? _current;
	private bool _disposed;

	public event Action<LinuxNotificationActivation>? Activated;
	public event Action<uint>? Closed;
	public event Action? Invalidated;

	public async Task<bool> IsAvailableAsync(CancellationToken ct) {
		if (_address is null) {
			return false;
		}
		try {
			var current = await EnsureConnectedAsync(ct).ConfigureAwait(false);
			return current.SupportsActions;
		} catch (DBusErrorReplyException ex) when (
			ex.ErrorName is "org.freedesktop.DBus.Error.ServiceUnknown"
				or "org.freedesktop.DBus.Error.NameHasNoOwner") {
			return false;
		}
	}

	public async Task<uint> ShowAsync(LinuxNotificationRequest notification, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(notification);
		var current = await EnsureConnectedAsync(ct).ConfigureAwait(false);
		if (!current.SupportsActions) {
			throw new InvalidOperationException("The desktop notification service does not support actions.");
		}
		return await current.Notifications.NotifyAsync(
			notification.AppName,
			notification.ReplacesId,
			notification.AppIcon,
			notification.Title,
			notification.Body,
			[.. notification.Actions],
			new Dictionary<string, VariantValue> {
				["desktop-entry"] = notification.DesktopEntry,
				["suppress-sound"] = notification.SuppressSound,
			},
			notification.ExpireTimeout).WaitAsync(ct).ConfigureAwait(false);
	}

	public async Task CloseAsync(uint id, CancellationToken ct) {
		var current = await EnsureConnectedAsync(ct).ConfigureAwait(false);
		await current.Notifications.CloseNotificationAsync(id).WaitAsync(ct).ConfigureAwait(false);
	}

	private async Task<NotificationConnection> EnsureConnectedAsync(CancellationToken ct) {
		string address = _address
			?? throw new InvalidOperationException("The desktop session has no D-Bus session bus.");
		await _connectionGate.WaitAsync(ct).ConfigureAwait(false);
		try {
			lock (_gate) {
				ObjectDisposedException.ThrowIf(_disposed, this);
				if (_current is not null) {
					return _current;
				}
			}

			var connection = new DBusConnection(address);
			NotificationConnection? current = null;
			try {
				await connection.ConnectAsync().AsTask().WaitAsync(ct).ConfigureAwait(false);
				var notifications = new DesktopNotificationProxy(connection, Destination, Path);
				string[] capabilities = await notifications.GetCapabilitiesAsync().WaitAsync(ct).ConfigureAwait(false);
				current = new NotificationConnection(
					connection,
					notifications,
					capabilities.Contains("actions", StringComparer.Ordinal));
				current.Subscriptions.Add(await notifications.WatchActivationTokenAsync(
					OnActivationToken,
					ObserverFlags.EmitAll,
					emitOnCapturedContext: false,
					state: current).AsTask().WaitAsync(ct).ConfigureAwait(false));
				current.Subscriptions.Add(await notifications.WatchActionInvokedAsync(
					OnActionInvoked,
					ObserverFlags.EmitAll,
					emitOnCapturedContext: false,
					state: current).AsTask().WaitAsync(ct).ConfigureAwait(false));
				current.Subscriptions.Add(await notifications.WatchNotificationClosedAsync(
					OnNotificationClosed,
					ObserverFlags.EmitAll,
					emitOnCapturedContext: false,
					state: current).AsTask().WaitAsync(ct).ConfigureAwait(false));
				lock (_gate) {
					ObjectDisposedException.ThrowIf(_disposed, this);
					_current = current;
				}
				return current;
			} catch {
				if (current is not null) {
					current.Dispose();
				} else {
					connection.Dispose();
				}
				throw;
			}
		} finally {
			_connectionGate.Release();
		}
	}

	private void OnActivationToken(
		Tmds.DBus.Protocol.Notification<(uint Id, string ActivationToken)> notification) {
		if (Current(notification) is not { } current) {
			return;
		}
		lock (_gate) {
			if (ReferenceEquals(_current, current)) {
				_activationTokens[notification.Value.Id] = notification.Value.ActivationToken;
			}
		}
	}

	private void OnActionInvoked(
		Tmds.DBus.Protocol.Notification<(uint Id, string ActionKey)> notification) {
		if (Current(notification) is not { } current) {
			return;
		}
		string? token;
		lock (_gate) {
			if (!ReferenceEquals(_current, current)) {
				return;
			}
			_activationTokens.Remove(notification.Value.Id, out token);
		}
		Activated?.Invoke(new LinuxNotificationActivation(
			notification.Value.Id,
			notification.Value.ActionKey,
			token));
	}

	private void OnNotificationClosed(
		Tmds.DBus.Protocol.Notification<(uint Id, uint Reason)> notification) {
		if (Current(notification) is not { } current) {
			return;
		}
		lock (_gate) {
			if (!ReferenceEquals(_current, current)) {
				return;
			}
			_activationTokens.Remove(notification.Value.Id);
		}
		Closed?.Invoke(notification.Value.Id);
	}

	private NotificationConnection? Current<T>(Tmds.DBus.Protocol.Notification<T> notification) {
		if (notification.State is not NotificationConnection current) {
			return null;
		}
		if (!notification.HasValue) {
			Invalidate(current);
			return null;
		}
		lock (_gate) {
			return !_disposed && ReferenceEquals(_current, current) ? current : null;
		}
	}

	private void Invalidate(NotificationConnection current) {
		lock (_gate) {
			if (_disposed || !ReferenceEquals(_current, current)) {
				return;
			}
			_current = null;
			_activationTokens.Clear();
		}
		current.Dispose();
		Invalidated?.Invoke();
	}

	public void Dispose() {
		NotificationConnection? current;
		lock (_gate) {
			if (_disposed) {
				return;
			}
			_disposed = true;
			current = _current;
			_current = null;
			_activationTokens.Clear();
		}
		current?.Dispose();
		_connectionGate.Dispose();
	}

	private sealed class NotificationConnection(
		DBusConnection connection,
		DesktopNotificationProxy notifications,
		bool supportsActions) : IDisposable {
		internal DesktopNotificationProxy Notifications { get; } = notifications;
		internal bool SupportsActions { get; } = supportsActions;
		internal List<IDisposable> Subscriptions { get; } = [];

		public void Dispose() {
			foreach (var subscription in Subscriptions) {
				subscription.Dispose();
			}
			connection.Dispose();
		}
	}
}
