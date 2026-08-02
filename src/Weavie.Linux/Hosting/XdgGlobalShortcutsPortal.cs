using Tmds.DBus.Protocol;
using Weavie.Linux.Portal;

namespace Weavie.Linux.Hosting;

internal sealed partial class XdgGlobalShortcutsPortal : IGlobalShortcutsPortal {
	private const string Destination = "org.freedesktop.portal.Desktop";
	private const string DesktopPath = "/org/freedesktop/portal/desktop";
	private readonly string _address;
	private readonly PortalHostIdentity _identity;
	private readonly object _stateGate = new();
	private readonly List<RetiredConnection> _retiredConnections = [];
	private DBusConnection _connection;
	private NameOwnerWatcher? _ownerWatcher;
	private GlobalShortcuts? _shortcuts;
	private string? _portalDestination;
	private IDisposable? _activationSubscription;
	private IDisposable? _sessionSubscription;
	private SessionWatch? _sessionWatch;
	private long _ownerGeneration;
	private bool _connected;
	private bool _disposed;

	internal XdgGlobalShortcutsPortal()
		: this(new LinuxDesktopAppScope()) { }

	internal XdgGlobalShortcutsPortal(ILinuxDesktopAppScope appScope) {
		ArgumentNullException.ThrowIfNull(appScope);
		_identity = new PortalHostIdentity(appScope);
		_address = DBusAddress.Session
			?? throw new InvalidOperationException("The desktop session has no D-Bus session bus.");
		_connection = new DBusConnection(_address);
	}

	public event Action<PortalActivation>? Activated;
	public event Action? Invalidated;
	public event Action<string>? Log;

	public async Task<PortalBinding> BindAsync(IReadOnlyList<PortalShortcut> shortcuts, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(shortcuts);
		var connected = await GetConnectedAsync(ct).ConfigureAwait(false);
		var portal = connected.Shortcuts;

		string createToken = Token("create");
		string sessionToken = Token("session");
		var (createStatus, createResults) = await RequestAsync(
			connected,
			createToken,
			() => portal.CreateSessionAsync(new Dictionary<string, VariantValue> {
				["handle_token"] = createToken,
				["session_handle_token"] = sessionToken,
			}),
			ct).ConfigureAwait(false);
		RequireSuccess(createStatus, "create a global-shortcut session");
		if (!createResults.TryGetValue("session_handle", out var sessionValue)
			|| sessionValue.Type != VariantValueType.String) {
			throw new InvalidOperationException("The global-shortcuts portal returned no session handle.");
		}
		string sessionHandle = sessionValue.GetString();
		var sessionWatch = await WatchSessionAsync(connected, sessionHandle).ConfigureAwait(false);

		try {
			string bindToken = Token("bind");
			var definitions = shortcuts.Select(shortcut => (
				shortcut.Id,
				new Dictionary<string, VariantValue> {
					["description"] = shortcut.Description,
					["preferred_trigger"] = shortcut.Trigger,
				})).ToArray();
			var (bindStatus, bindResults) = await RequestAsync(
				connected,
				bindToken,
				() => portal.BindShortcutsAsync(sessionHandle, definitions, string.Empty, new Dictionary<string, VariantValue> {
					["handle_token"] = bindToken,
				}),
				ct).ConfigureAwait(false);
			RequireSuccess(bindStatus, "bind the global shortcuts");
			var requested = shortcuts.Select(shortcut => shortcut.Id).ToHashSet(StringComparer.Ordinal);
			return new PortalBinding(sessionHandle, ReadBoundShortcutIds(bindResults, requested));
		} catch {
			ClearSessionWatch(sessionWatch);
			await CloseSessionAfterFailedBindAsync(connected, sessionHandle).ConfigureAwait(false);
			throw;
		}
	}

	public async Task CloseSessionAsync(string sessionHandle) {
		ArgumentException.ThrowIfNullOrEmpty(sessionHandle);
		ConnectedPortal? connected;
		lock (_stateGate) {
			connected = _connected && _shortcuts is not null && _portalDestination is not null
				? new ConnectedPortal(_connection, _portalDestination, _shortcuts, _ownerGeneration)
				: null;
		}
		try {
			if (connected is not null) {
				await CloseSessionAsync(connected, sessionHandle).ConfigureAwait(false);
			}
		} catch (Exception ex) when (ex is DBusOwnerChangedException or DBusConnectionException) {
		} finally {
			ClearSessionWatch(sessionHandle);
		}
	}

	public void Dispose() {
		IDisposable? activationSubscription;
		IDisposable? sessionSubscription;
		NameOwnerWatcher? ownerWatcher;
		DBusConnection connection;
		RetiredConnection[] retired;
		lock (_stateGate) {
			if (_disposed) {
				return;
			}
			_disposed = true;
			activationSubscription = _activationSubscription;
			sessionSubscription = _sessionSubscription;
			ownerWatcher = _ownerWatcher;
			connection = _connection;
			retired = [.. _retiredConnections];
			_activationSubscription = null;
			_sessionSubscription = null;
			_sessionWatch = null;
			_ownerWatcher = null;
			_shortcuts = null;
			_portalDestination = null;
			_connected = false;
		}

		activationSubscription?.Dispose();
		sessionSubscription?.Dispose();
		ownerWatcher?.Dispose();
		connection.Dispose();
		foreach (var item in retired) {
			item.OwnerWatcher?.Dispose();
			item.Connection.Dispose();
		}
	}

	private async Task<SessionWatch> WatchSessionAsync(ConnectedPortal connected, string sessionHandle) {
		var watch = new SessionWatch(sessionHandle);
		var session = new Weavie.Linux.Portal.Session(
			connected.Connection,
			connected.Destination,
			sessionHandle);
		var subscription = await session.WatchClosedAsync(
			_ => OnSessionClosed(watch),
			emitOnCapturedContext: false).ConfigureAwait(false);
		IDisposable? previous;
		lock (_stateGate) {
			if (_disposed
				|| watch.Closed
				|| !_connected
				|| !ReferenceEquals(connected.Connection, _connection)
				|| connected.Generation != _ownerGeneration
				|| !string.Equals(connected.Destination, _portalDestination, StringComparison.Ordinal)) {
				subscription.Dispose();
				throw new DBusOwnerChangedException("The desktop portal changed while creating a shortcut session.");
			}
			previous = _sessionSubscription;
			_sessionSubscription = subscription;
			_sessionWatch = watch;
		}
		previous?.Dispose();
		return watch;
	}

	private void OnSessionClosed(SessionWatch watch) {
		IDisposable? subscription = null;
		bool notify = false;
		lock (_stateGate) {
			watch.Closed = true;
			if (!_disposed && ReferenceEquals(watch, _sessionWatch)) {
				subscription = _sessionSubscription;
				_sessionSubscription = null;
				_sessionWatch = null;
				notify = true;
			}
		}
		subscription?.Dispose();
		if (notify) {
			Log?.Invoke("[hotkey] the desktop closed the global-shortcut session; restoring shortcuts.");
			Invalidated?.Invoke();
		}
	}

	private void ClearSessionWatch(string sessionHandle) {
		SessionWatch? watch;
		lock (_stateGate) {
			watch = string.Equals(_sessionWatch?.SessionHandle, sessionHandle, StringComparison.Ordinal)
				? _sessionWatch
				: null;
		}
		if (watch is not null) {
			ClearSessionWatch(watch);
		}
	}

	private void ClearSessionWatch(SessionWatch watch) {
		IDisposable? subscription = null;
		lock (_stateGate) {
			if (ReferenceEquals(watch, _sessionWatch)) {
				subscription = _sessionSubscription;
				_sessionSubscription = null;
				_sessionWatch = null;
			}
		}
		subscription?.Dispose();
	}
}
