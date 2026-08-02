using Tmds.DBus.Protocol;
using Weavie.Linux.Portal;

namespace Weavie.Linux.Hosting;

internal sealed partial class XdgGlobalShortcutsPortal {
	private static readonly TimeSpan ConnectionRetryDelay = TimeSpan.FromMilliseconds(250);

	private async Task<ConnectedPortal> GetConnectedAsync(CancellationToken ct) {
		LinuxDesktopIdentity.EnsureInstalled();
		while (true) {
			await EnsureConnectedAsync(ct).ConfigureAwait(false);
			lock (_stateGate) {
				if (_connected && _shortcuts is not null && _portalDestination is not null) {
					return new ConnectedPortal(
						_connection,
						_portalDestination,
						_shortcuts,
						_ownerGeneration);
				}
			}
		}
	}

	private async Task EnsureConnectedAsync(CancellationToken ct) {
		bool connectionFailureReported = false;
		while (true) {
			DBusConnection connection;
			NameOwnerWatcher? ownerWatcher;
			lock (_stateGate) {
				ObjectDisposedException.ThrowIf(_disposed, this);
				if (_connected) {
					return;
				}
				connection = _connection;
				ownerWatcher = _ownerWatcher;
			}

			try {
				await connection.ConnectAsync().ConfigureAwait(false);
				ct.ThrowIfCancellationRequested();
				ownerWatcher = await GetOwnerWatcherAsync(connection, ownerWatcher).ConfigureAwait(false);
				await ActivatePortalAsync(connection).ConfigureAwait(false);
				string owner = await ownerWatcher.WaitForOwnerAsync(ct).ConfigureAwait(false);
				long generation;
				lock (_stateGate) {
					if (!ReferenceEquals(connection, _connection)) {
						continue;
					}
					generation = _ownerGeneration;
				}

				await RegisterIdentityAsync(connection, owner, ct).ConfigureAwait(false);
				var shortcuts = new GlobalShortcuts(connection, owner, DesktopPath);
				var watchState = new ActivationWatch(connection, generation);
				var subscription = await shortcuts.WatchActivatedAsync(
					OnActivated,
					ObserverFlags.EmitOnOwnerChanged
						| ObserverFlags.EmitOnConnectionClosed
						| ObserverFlags.EmitOnConnectionFailed
						| ObserverFlags.EmitOnReaderFailed,
					emitOnCapturedContext: false,
					state: watchState).ConfigureAwait(false);
				string? currentOwner = ownerWatcher.GetCurrentOwner();
				bool published;
				lock (_stateGate) {
					published = !_disposed
						&& ReferenceEquals(connection, _connection)
						&& SetupIsCurrent(generation, _ownerGeneration, owner, currentOwner);
					if (published) {
						_shortcuts = shortcuts;
						_portalDestination = owner;
						_activationSubscription = subscription;
						_connected = true;
					}
				}
				if (published) {
					if (connectionFailureReported) {
						Log?.Invoke("[hotkey] restored the desktop portal connection.");
					}
					return;
				}
				subscription.Dispose();
			} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
				throw;
			} catch (DBusOwnerChangedException) {
				continue;
			} catch (DBusConnectionException ex) {
				if (!ReplaceFailedConnection(connection)) {
					continue;
				}
				if (!connectionFailureReported) {
					Log?.Invoke($"[hotkey] desktop portal connection failed ({ex.Message}); reconnecting.");
					connectionFailureReported = true;
				}
				await Task.Delay(ConnectionRetryDelay, ct).ConfigureAwait(false);
			}
		}
	}

	private async Task<NameOwnerWatcher> GetOwnerWatcherAsync(
		DBusConnection connection,
		NameOwnerWatcher? ownerWatcher) {
		if (ownerWatcher is not null) {
			return ownerWatcher;
		}

		var created = await connection.WatchNameOwnerAsync(Destination).ConfigureAwait(false);
		lock (_stateGate) {
			if (!ReferenceEquals(connection, _connection)) {
				created.Dispose();
				throw new DBusOwnerChangedException("The desktop portal connection changed during setup.");
			}
			_ownerWatcher ??= created;
			ownerWatcher = _ownerWatcher;
		}
		if (!ReferenceEquals(created, ownerWatcher)) {
			created.Dispose();
		}
		return ownerWatcher;
	}

	private static async Task ActivatePortalAsync(DBusConnection connection) {
		var bus = new Weavie.Linux.Bus.DBus(
			connection,
			"org.freedesktop.DBus",
			"/org/freedesktop/DBus");
		uint result = await bus.StartServiceByNameAsync(Destination, 0).ConfigureAwait(false);
		if (result is not 1 and not 2) {
			throw new InvalidOperationException($"D-Bus could not activate the desktop portal (result {result}).");
		}
	}

	private async Task RegisterIdentityAsync(DBusConnection connection, string owner, CancellationToken ct) {
		var registry = new Registry(connection, owner, DesktopPath);
		await _identity.RegisterAsync(
			() => registry.RegisterAsync(LinuxDesktopIdentity.AppId, []),
			message => Log?.Invoke(message),
			ct).ConfigureAwait(false);
	}

	private void OnActivated(
		Notification<(ObjectPath SessionHandle, string ShortcutId, ulong Timestamp, Dictionary<string, VariantValue> Options)> notification) {
		if (notification.State is not ActivationWatch watchState) {
			return;
		}
		if (!notification.HasValue) {
			Invalidate(watchState, notification.Type);
			return;
		}
		lock (_stateGate) {
			if (_disposed
				|| !_connected
				|| !ReferenceEquals(watchState.Connection, _connection)
				|| watchState.Generation != _ownerGeneration) {
				return;
			}
		}

		var (sessionHandle, shortcutId, _, options) = notification.Value;
		string? token = options.TryGetValue("activation_token", out var activation)
			&& activation.Type == VariantValueType.String
			? activation.GetString()
			: null;
		Activated?.Invoke(new PortalActivation(sessionHandle, shortcutId, token));
	}

	private void Invalidate(ActivationWatch watchState, NotificationType reason) {
		IDisposable? sessionSubscription;
		bool replaceConnection;
		lock (_stateGate) {
			if (_disposed
				|| !ReferenceEquals(watchState.Connection, _connection)
				|| watchState.Generation != _ownerGeneration) {
				return;
			}

			_ownerGeneration++;
			_connected = false;
			_shortcuts = null;
			_portalDestination = null;
			_activationSubscription = null;
			sessionSubscription = _sessionSubscription;
			_sessionSubscription = null;
			_sessionWatch = null;
			replaceConnection = reason is not NotificationType.OwnerChanged;
			if (replaceConnection) {
				_retiredConnections.Add(new RetiredConnection(_ownerWatcher, _connection));
				_connection = new DBusConnection(_address);
				_ownerWatcher = null;
			}
		}
		sessionSubscription?.Dispose();

		Log?.Invoke(
			reason == NotificationType.OwnerChanged
				? "[hotkey] desktop portal restarted; restoring global shortcuts."
				: $"[hotkey] desktop portal connection ended ({reason}); reconnecting global shortcuts.");
		Invalidated?.Invoke();
	}

	private bool ReplaceFailedConnection(DBusConnection failed) {
		NameOwnerWatcher? ownerWatcher;
		lock (_stateGate) {
			if (_disposed) {
				throw new ObjectDisposedException(nameof(XdgGlobalShortcutsPortal));
			}
			if (!ReferenceEquals(failed, _connection)) {
				return false;
			}
			ownerWatcher = _ownerWatcher;
			_ownerGeneration++;
			_connection = new DBusConnection(_address);
			_ownerWatcher = null;
		}
		ownerWatcher?.Dispose();
		failed.Dispose();
		return true;
	}
}
