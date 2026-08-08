using Weavie.Core.Sessions;

namespace Weavie.Linux.Hosting;

internal sealed class LinuxNotificationService : IDisposable {
	private const string DefaultAction = "default";
	private readonly ILinuxNotificationTransport _transport;
	private readonly Action<string> _log;
	private readonly SemaphoreSlim _operations = new(1, 1);
	private readonly SystemNotificationRoutes<SystemNotificationChannel> _routes = new();
	private readonly Dictionary<Replacement, uint> _byReplacement = [];
	private readonly Dictionary<uint, Delivered> _byServerId = [];
	private readonly object _gate = new();
	private bool _disposed;

	public LinuxNotificationService(Action<string> log)
		: this(new FreedesktopNotificationTransport(), log) {
	}

	internal LinuxNotificationService(ILinuxNotificationTransport transport, Action<string> log) {
		ArgumentNullException.ThrowIfNull(transport);
		ArgumentNullException.ThrowIfNull(log);
		_transport = transport;
		_log = log;
		_transport.Activated += OnActivated;
		_transport.Closed += OnClosed;
		_transport.Invalidated += OnInvalidated;
	}

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

	internal async Task<SystemNotificationPermission> GetPermissionAsync(CancellationToken ct) =>
		await _transport.IsAvailableAsync(ct).ConfigureAwait(false)
			? SystemNotificationPermission.Granted
			: SystemNotificationPermission.Unavailable;

	internal Task<SystemNotificationPermission> RequestPermissionAsync(CancellationToken ct) =>
		GetPermissionAsync(ct);

	internal async Task ShowAsync(
		SystemNotificationChannel channel,
		SystemNotification notification,
		CancellationToken ct) {
		await _operations.WaitAsync(ct).ConfigureAwait(false);
		try {
			SystemNotificationRouteRegistration registration;
			uint replacesId;
			lock (_gate) {
				ObjectDisposedException.ThrowIf(_disposed, this);
				registration = _routes.Replace(channel, notification);
				_byReplacement.TryGetValue(new Replacement(channel, notification.ReplacementId), out replacesId);
			}

			try {
				uint id = await _transport.ShowAsync(
					new LinuxNotificationRequest(
						replacesId,
						"Weavie",
						LinuxDesktopIdentity.AppId,
						LinuxDesktopIdentity.AppId,
						notification.Title,
						notification.Body,
						[DefaultAction, "Open Weavie"],
						SuppressSound: true,
						ExpireTimeout: -1),
					ct).ConfigureAwait(false);
				if (id == 0) {
					throw new InvalidOperationException("The desktop notification service returned an invalid id.");
				}
				lock (_gate) {
					ObjectDisposedException.ThrowIf(_disposed, this);
					AdoptServerId(
						id,
						new Delivered(channel, notification.ReplacementId, notification.ActivationId));
				}
				registration.Commit();
			} catch {
				registration.Rollback();
				throw;
			}
		} finally {
			_operations.Release();
		}
	}

	internal async Task RemoveAsync(
		SystemNotificationChannel channel,
		string replacementId,
		CancellationToken ct) {
		await _operations.WaitAsync(ct).ConfigureAwait(false);
		try {
			var replacement = new Replacement(channel, replacementId);
			uint id;
			lock (_gate) {
				ObjectDisposedException.ThrowIf(_disposed, this);
				if (!_byReplacement.TryGetValue(replacement, out id)) {
					_routes.Forget(channel, replacementId);
					return;
				}
			}
			await _transport.CloseAsync(id, ct).ConfigureAwait(false);
			Forget(id);
		} finally {
			_operations.Release();
		}
	}

	internal void DisposeChannel(SystemNotificationChannel channel) {
		lock (_gate) {
			_routes.ForgetOwner(channel);
			foreach (var replacement in _byReplacement.Keys
				.Where(item => ReferenceEquals(item.Channel, channel)).ToArray()) {
				uint id = _byReplacement[replacement];
				_byReplacement.Remove(replacement);
				_byServerId.Remove(id);
			}
		}
	}

	private void AdoptServerId(uint id, Delivered delivered) {
		var replacement = new Replacement(delivered.Channel, delivered.ReplacementId);
		if (_byServerId.TryGetValue(id, out var displaced)
			&& (!ReferenceEquals(displaced.Channel, delivered.Channel)
				|| displaced.ReplacementId != delivered.ReplacementId)) {
			_routes.Forget(displaced.Channel, displaced.ReplacementId);
			_byReplacement.Remove(new Replacement(displaced.Channel, displaced.ReplacementId));
		}
		if (_byReplacement.TryGetValue(replacement, out uint previous) && previous != id) {
			_byServerId.Remove(previous);
		}
		_byReplacement[replacement] = id;
		_byServerId[id] = delivered;
	}

	private void OnActivated(LinuxNotificationActivation activation) {
		if (activation.Action != DefaultAction) {
			return;
		}
		Delivered? delivered;
		lock (_gate) {
			if (_disposed || !_byServerId.TryGetValue(activation.Id, out delivered)) {
				return;
			}
		}
		if (_routes.TryTake(delivered.ActivationId, out var owner)) {
			owner.Activate(delivered.ActivationId, activation.ActivationToken);
		}
	}

	private void OnClosed(uint id) => Forget(id);

	private void Forget(uint id) {
		lock (_gate) {
			if (_disposed || !_byServerId.Remove(id, out var delivered)) {
				return;
			}
			var replacement = new Replacement(delivered.Channel, delivered.ReplacementId);
			if (_byReplacement.TryGetValue(replacement, out uint current) && current == id) {
				_byReplacement.Remove(replacement);
			}
			_routes.Forget(delivered.Channel, delivered.ReplacementId);
		}
	}

	private void OnInvalidated() {
		lock (_gate) {
			if (_disposed) {
				return;
			}
			_byReplacement.Clear();
			_byServerId.Clear();
			_routes.Clear();
		}
		_log("[notifications] the desktop notification service changed; reconnecting on the next request.");
	}

	public void Dispose() {
		lock (_gate) {
			if (_disposed) {
				return;
			}
			_disposed = true;
			_byReplacement.Clear();
			_byServerId.Clear();
			_routes.Clear();
		}
		_transport.Activated -= OnActivated;
		_transport.Closed -= OnClosed;
		_transport.Invalidated -= OnInvalidated;
		_transport.Dispose();
		_operations.Dispose();
	}

	private sealed record Replacement(SystemNotificationChannel Channel, string Id);
	private sealed record Delivered(
		SystemNotificationChannel Channel,
		string ReplacementId,
		string ActivationId);
}
