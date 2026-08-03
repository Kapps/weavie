using Tmds.DBus.Protocol;
using Weavie.Core.Sessions;
using PortalNotification = Weavie.Linux.Portal.Notification;

namespace Weavie.Linux.Hosting;

/// <summary>The process-wide XDG desktop notification portal and workspace-channel router.</summary>
internal sealed class XdgNotificationService : IDisposable {
	private const string DefaultAction = "default";
	private readonly string? _address = DBusAddress.Session;
	private readonly Action<string> _log;
	private readonly SemaphoreSlim _connectionGate = new(1, 1);
	private readonly SystemNotificationRoutes<XdgNotificationChannel> _routes = new();
	private readonly object _gate = new();
	private PortalConnection? _current;
	private bool _disposed;

	public XdgNotificationService(Action<string> log) {
		ArgumentNullException.ThrowIfNull(log);
		_log = log;
	}

	/// <summary>Creates one workspace-owned channel on the shared notification portal.</summary>
	public XdgNotificationChannel CreateChannel() {
		lock (_gate) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			return new XdgNotificationChannel(this);
		}
	}

	internal async Task<SystemNotificationPermission> GetPermissionAsync(CancellationToken ct) {
		if (_address is null) {
			return SystemNotificationPermission.Unavailable;
		}
		_ = await EnsureConnectedAsync(ct).ConfigureAwait(false);
		return SystemNotificationPermission.Granted;
	}

	internal Task<SystemNotificationPermission> RequestPermissionAsync(CancellationToken ct) =>
		GetPermissionAsync(ct);

	internal async Task ShowAsync(
		XdgNotificationChannel channel,
		SystemNotification notification,
		CancellationToken ct) {
		var portal = await EnsureConnectedAsync(ct).ConfigureAwait(false);
		ct.ThrowIfCancellationRequested();
		SystemNotificationRouteRegistration registration;
		lock (_gate) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			registration = _routes.Replace(channel, notification);
		}

		try {
			await portal.Notifications.AddNotificationAsync(
				notification.ReplacementId,
				new Dictionary<string, VariantValue> {
					["title"] = notification.Title,
					["body"] = notification.Body,
					["priority"] = "normal",
					["sound"] = "silent",
					["default-action"] = DefaultAction,
					["default-action-target"] = notification.ActivationId,
				}).ConfigureAwait(false);
			registration.Commit();
		} catch {
			registration.Rollback();
			throw;
		}
	}

	internal async Task RemoveAsync(
		XdgNotificationChannel channel,
		string replacementId,
		CancellationToken ct) {
		var portal = await EnsureConnectedAsync(ct).ConfigureAwait(false);
		Forget(channel, replacementId);
		await portal.Notifications.RemoveNotificationAsync(replacementId).WaitAsync(ct).ConfigureAwait(false);
	}

	internal void DisposeChannel(XdgNotificationChannel channel) =>
		_routes.ForgetOwner(channel);

	private async Task<PortalConnection> EnsureConnectedAsync(CancellationToken ct) {
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
			PortalConnection? portal = null;
			try {
				await connection.ConnectAsync().AsTask().WaitAsync(ct).ConfigureAwait(false);
				portal = new PortalConnection(
					connection,
					new PortalNotification(connection, XdgDesktopPortal.Destination, XdgDesktopPortal.Path));
				await XdgDesktopPortal.RegisterIdentityAsync(
					connection,
					line => _log($"[notifications] {line}")).WaitAsync(ct).ConfigureAwait(false);
				portal.Subscription = await portal.Notifications.WatchActionInvokedAsync(
					OnActionInvoked,
					ObserverFlags.EmitOnOwnerChanged
						| ObserverFlags.EmitOnConnectionClosed
						| ObserverFlags.EmitOnConnectionFailed
						| ObserverFlags.EmitOnReaderFailed,
					emitOnCapturedContext: false,
					state: portal).AsTask().WaitAsync(ct).ConfigureAwait(false);
				lock (_gate) {
					ObjectDisposedException.ThrowIf(_disposed, this);
					_current = portal;
				}
				return portal;
			} catch {
				if (portal is not null) {
					portal.Dispose();
				} else {
					connection.Dispose();
				}
				throw;
			}
		} finally {
			_connectionGate.Release();
		}
	}

	private void OnActionInvoked(
		Tmds.DBus.Protocol.Notification<(string Id, string Action, VariantValue[] Parameter)> notification) {
		if (notification.State is not PortalConnection portal) {
			return;
		}
		if (!notification.HasValue) {
			Invalidate(portal, $"desktop portal changed ({notification.Type})");
			return;
		}

		var (_, action, parameters) = notification.Value;
		if (action != DefaultAction || parameters.Length == 0) {
			return;
		}
		var activation = Unwrap(parameters[0]);
		if (activation.Type != VariantValueType.String) {
			return;
		}

		string activationId = activation.GetString();
		lock (_gate) {
			if (_disposed || !ReferenceEquals(_current, portal)) {
				return;
			}
		}
		if (_routes.TryTake(activationId, out var owner)) {
			owner.Activate(activationId, ActivationToken(parameters));
		}
	}

	private static string? ActivationToken(VariantValue[] parameters) {
		foreach (var parameter in parameters.Skip(1)) {
			var value = Unwrap(parameter);
			if (value.Type == VariantValueType.Dictionary
				&& value.GetDictionary<string, VariantValue>().TryGetValue("activation-token", out var token)) {
				token = Unwrap(token);
				if (token.Type == VariantValueType.String) {
					return token.GetString();
				}
			}
		}
		return null;
	}

	private static VariantValue Unwrap(VariantValue value) =>
		value.Type == VariantValueType.Variant ? value.GetVariantValue() : value;

	private void Invalidate(PortalConnection portal, string reason) {
		lock (_gate) {
			if (_disposed || !ReferenceEquals(_current, portal)) {
				return;
			}
			_current = null;
		}
		portal.Dispose();
		_log($"[notifications] {reason}; reconnecting when the next notification arrives.");
	}

	private void Forget(XdgNotificationChannel channel, string replacementId) =>
		_routes.Forget(channel, replacementId);

	/// <inheritdoc/>
	public void Dispose() {
		PortalConnection? portal;
		lock (_gate) {
			if (_disposed) {
				return;
			}
			_disposed = true;
			portal = _current;
			_current = null;
			_routes.Clear();
		}
		portal?.Dispose();
		_connectionGate.Dispose();
	}

	private sealed class PortalConnection(DBusConnection connection, PortalNotification notifications) : IDisposable {
		internal DBusConnection Connection { get; } = connection;
		internal PortalNotification Notifications { get; } = notifications;
		internal IDisposable? Subscription { get; set; }

		public void Dispose() {
			Subscription?.Dispose();
			Connection.Dispose();
		}
	}
}

/// <summary>One Linux workspace window's notification identities and activation event.</summary>
internal sealed class XdgNotificationChannel : ISystemNotificationChannel, IDisposable {
	private readonly XdgNotificationService _service;
	private bool _disposed;

	internal XdgNotificationChannel(XdgNotificationService service) {
		_service = service;
	}

	/// <inheritdoc/>
	public event Action<SystemNotificationActivation>? Activated;

	/// <inheritdoc/>
	public Task<SystemNotificationPermission> GetPermissionAsync(CancellationToken ct) =>
		_service.GetPermissionAsync(ct);

	/// <inheritdoc/>
	public Task<SystemNotificationPermission> RequestPermissionAsync(CancellationToken ct) =>
		_service.RequestPermissionAsync(ct);

	/// <inheritdoc/>
	public Task ShowAsync(SystemNotification notification, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(notification);
		ObjectDisposedException.ThrowIf(_disposed, this);
		return _service.ShowAsync(this, notification, ct);
	}

	/// <inheritdoc/>
	public Task RemoveAsync(string replacementId, CancellationToken ct) {
		ArgumentException.ThrowIfNullOrEmpty(replacementId);
		ObjectDisposedException.ThrowIf(_disposed, this);
		return _service.RemoveAsync(this, replacementId, ct);
	}

	internal void Activate(string activationId, string? activationToken) {
		if (!_disposed) {
			Activated?.Invoke(new SystemNotificationActivation(activationId, activationToken));
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
