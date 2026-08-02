using Tmds.DBus.Protocol;
using Weavie.Linux.Portal;

namespace Weavie.Linux.Hosting;

internal sealed class XdgGlobalShortcutsPortal : IGlobalShortcutsPortal {
	private const string Destination = "org.freedesktop.portal.Desktop";
	private const string DesktopPath = "/org/freedesktop/portal/desktop";
	private readonly string _address = DBusAddress.Session
		?? throw new InvalidOperationException("The desktop session has no D-Bus session bus.");
	private readonly object _gate = new();
	private PortalConnection? _current;
	private DBusConnection? _connecting;
	private SessionWatch? _session;
	private bool _disposed;

	public event Action<PortalActivation>? Activated;
	public event Action? Invalidated;
	public event Action<string>? Log;

	public async Task<PortalBinding> BindAsync(IReadOnlyList<PortalShortcut> shortcuts) {
		ArgumentNullException.ThrowIfNull(shortcuts);
		var portal = await EnsureConnectedAsync().ConfigureAwait(false);
		string createToken = Token("create");
		var (createStatus, createResults) = await RequestAsync(
			portal,
			createToken,
			() => portal.Shortcuts.CreateSessionAsync(new Dictionary<string, VariantValue> {
				["handle_token"] = createToken,
				["session_handle_token"] = Token("session"),
			})).ConfigureAwait(false);
		RequireSuccess(createStatus, "create a global-shortcut session");
		if (!createResults.TryGetValue("session_handle", out var sessionValue)
			|| sessionValue.Type != VariantValueType.String) {
			throw new InvalidOperationException("The global-shortcuts portal returned no session handle.");
		}

		string sessionHandle = sessionValue.GetString();
		var sessionWatch = await WatchSessionAsync(portal, sessionHandle).ConfigureAwait(false);
		try {
			string bindToken = Token("bind");
			var definitions = shortcuts.Select(shortcut => (
				shortcut.Id,
				new Dictionary<string, VariantValue> {
					["description"] = shortcut.Description,
					["preferred_trigger"] = shortcut.Trigger,
				})).ToArray();
			var (bindStatus, bindResults) = await RequestAsync(
				portal,
				bindToken,
				() => portal.Shortcuts.BindShortcutsAsync(
					sessionHandle,
					definitions,
					string.Empty,
					new Dictionary<string, VariantValue> { ["handle_token"] = bindToken })).ConfigureAwait(false);
			RequireSuccess(bindStatus, "bind the global shortcuts");
			var requested = shortcuts.Select(shortcut => shortcut.Id).ToHashSet(StringComparer.Ordinal);
			return new PortalBinding(sessionHandle, ReadBoundShortcutIds(bindResults, requested));
		} catch {
			ClearSessionWatch(sessionWatch);
			await CloseAfterFailedBindAsync(portal, sessionHandle).ConfigureAwait(false);
			throw;
		}
	}

	public async Task CloseSessionAsync(string sessionHandle) {
		ArgumentException.ThrowIfNullOrEmpty(sessionHandle);
		ClearSessionWatch(sessionHandle);
		PortalConnection? portal;
		lock (_gate) {
			portal = _current;
		}
		if (portal is not null) {
			await CloseSessionAsync(portal, sessionHandle).ConfigureAwait(false);
		}
	}

	public void Dispose() {
		PortalConnection? portal;
		DBusConnection? connecting;
		SessionWatch? session;
		lock (_gate) {
			if (_disposed) {
				return;
			}
			_disposed = true;
			portal = _current;
			connecting = _connecting;
			session = _session;
			_current = null;
			_connecting = null;
			_session = null;
		}
		session?.Dispose();
		connecting?.Dispose();
		portal?.Dispose();
	}

	private async Task<PortalConnection> EnsureConnectedAsync() {
		DBusConnection connection;
		lock (_gate) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_current is not null) {
				return _current;
			}
			connection = new DBusConnection(_address);
			_connecting = connection;
		}

		PortalConnection? portal = null;
		try {
			await connection.ConnectAsync().ConfigureAwait(false);
			portal = new PortalConnection(
				connection,
				new GlobalShortcuts(connection, Destination, DesktopPath));
			lock (_gate) {
				ObjectDisposedException.ThrowIf(_disposed, this);
				_connecting = null;
				_current = portal;
			}
			var activationSubscription = await portal.Shortcuts.WatchActivatedAsync(
				OnActivated,
				ObserverFlags.EmitOnOwnerChanged
					| ObserverFlags.EmitOnConnectionClosed
					| ObserverFlags.EmitOnConnectionFailed
					| ObserverFlags.EmitOnReaderFailed,
				emitOnCapturedContext: false,
				state: portal).ConfigureAwait(false);
			lock (_gate) {
				if (_disposed || !ReferenceEquals(_current, portal)) {
					activationSubscription.Dispose();
					throw new InvalidOperationException("The desktop portal changed while connecting.");
				}
				portal.ActivationSubscription = activationSubscription;
			}
			await RegisterIdentityAsync(connection).ConfigureAwait(false);
			lock (_gate) {
				if (_disposed || !ReferenceEquals(_current, portal)) {
					throw new InvalidOperationException("The desktop portal changed while registering Weavie.");
				}
			}
			return portal;
		} catch {
			lock (_gate) {
				if (ReferenceEquals(_connecting, connection)) {
					_connecting = null;
				}
				if (ReferenceEquals(_current, portal)) {
					_current = null;
				}
			}
			if (portal is not null) {
				portal.Dispose();
			} else {
				connection.Dispose();
			}
			throw;
		}
	}

	private async Task RegisterIdentityAsync(DBusConnection connection) {
		var registry = new Registry(connection, Destination, DesktopPath);
		try {
			await registry.RegisterAsync(LinuxDesktopIdentity.AppId, []).ConfigureAwait(false);
		} catch (DBusErrorReplyException ex) when (ex.ErrorName == "org.freedesktop.portal.Error.NotAllowed") {
			Log?.Invoke("[hotkey] the sandbox supplies Weavie's desktop portal identity.");
		} catch (DBusErrorReplyException ex) when (
			ex.ErrorName is "org.freedesktop.DBus.Error.UnknownInterface"
				or "org.freedesktop.DBus.Error.UnknownMethod") {
			Log?.Invoke("[hotkey] the desktop portal is using its built-in host application identity detection.");
		}
	}

	private async Task<SessionWatch> WatchSessionAsync(PortalConnection portal, string sessionHandle) {
		var session = new Weavie.Linux.Portal.Session(portal.Connection, Destination, sessionHandle);
		var watch = new SessionWatch(sessionHandle);
		SessionWatch? previous;
		lock (_gate) {
			if (_disposed || !ReferenceEquals(portal, _current)) {
				throw new InvalidOperationException("The desktop portal changed while creating a shortcut session.");
			}
			previous = _session;
			_session = watch;
		}
		previous?.Dispose();
		try {
			var subscription = await session.WatchClosedAsync(
				_ => OnSessionClosed(watch),
				emitOnCapturedContext: false).ConfigureAwait(false);
			lock (_gate) {
				if (_disposed || !ReferenceEquals(portal, _current) || !ReferenceEquals(watch, _session)) {
					subscription.Dispose();
					throw new InvalidOperationException("The shortcut session closed while it was being created.");
				}
				watch.Subscription = subscription;
			}
			return watch;
		} catch {
			ClearSessionWatch(watch);
			throw;
		}
	}

	private async Task<(uint Response, Dictionary<string, VariantValue> Results)> RequestAsync(
		PortalConnection portal,
		string token,
		Func<Task<ObjectPath>> invoke) {
		string sender = portal.Connection.UniqueName
			?? throw new InvalidOperationException("The D-Bus session connection has no unique name.");
		string senderPath = sender[1..].Replace(".", "_", StringComparison.Ordinal);
		var expectedHandle = new ObjectPath($"/org/freedesktop/portal/desktop/request/{senderPath}/{token}");
		var request = new Request(portal.Connection, Destination, expectedHandle);
		var response = new TaskCompletionSource<(uint, Dictionary<string, VariantValue>)>(
			TaskCreationOptions.RunContinuationsAsynchronously);
		using var subscription = await request.WatchResponseAsync(
			notification => {
				if (notification.HasValue) {
					response.TrySetResult(notification.Value);
				} else {
					response.TrySetException(notification.Exception);
				}
			},
			ObserverFlags.EmitAll,
			emitOnCapturedContext: false,
			state: null).ConfigureAwait(false);
		var actualHandle = await invoke().ConfigureAwait(false);
		if (!string.Equals(actualHandle.ToString(), expectedHandle.ToString(), StringComparison.Ordinal)) {
			throw new InvalidOperationException(
				$"The portal returned request handle '{actualHandle}' instead of '{expectedHandle}'.");
		}
		return await response.Task.ConfigureAwait(false);
	}

	private void OnActivated(
		Notification<(ObjectPath SessionHandle, string ShortcutId, ulong Timestamp, Dictionary<string, VariantValue> Options)> notification) {
		if (notification.State is not PortalConnection portal) {
			return;
		}
		if (!notification.HasValue) {
			Invalidate(portal, $"desktop portal changed ({notification.Type})");
			return;
		}
		lock (_gate) {
			if (_disposed || !ReferenceEquals(portal, _current)) {
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

	private void OnSessionClosed(SessionWatch watch) {
		bool notify;
		lock (_gate) {
			notify = !_disposed && ReferenceEquals(watch, _session);
			if (notify) {
				_session = null;
			}
		}
		watch.Dispose();
		if (notify) {
			Log?.Invoke("[hotkey] the desktop closed the global-shortcut session; restoring shortcuts.");
			Invalidated?.Invoke();
		}
	}

	private void Invalidate(PortalConnection portal, string reason) {
		SessionWatch? session;
		lock (_gate) {
			if (_disposed || !ReferenceEquals(portal, _current)) {
				return;
			}
			_current = null;
			session = _session;
			_session = null;
		}
		session?.Dispose();
		portal.Dispose();
		Log?.Invoke($"[hotkey] {reason}; restoring global shortcuts.");
		Invalidated?.Invoke();
	}

	private void ClearSessionWatch(string sessionHandle) {
		SessionWatch? watch;
		lock (_gate) {
			watch = string.Equals(_session?.SessionHandle, sessionHandle, StringComparison.Ordinal)
				? _session
				: null;
			if (watch is not null) {
				_session = null;
			}
		}
		watch?.Dispose();
	}

	private void ClearSessionWatch(SessionWatch watch) {
		lock (_gate) {
			if (ReferenceEquals(watch, _session)) {
				_session = null;
			}
		}
		watch.Dispose();
	}

	private static async Task CloseAfterFailedBindAsync(PortalConnection portal, string sessionHandle) {
		try {
			await CloseSessionAsync(portal, sessionHandle).ConfigureAwait(false);
		} catch (Exception ex) when (ex is DBusOwnerChangedException or DBusConnectionException) {
		}
	}

	private static Task CloseSessionAsync(PortalConnection portal, string sessionHandle) =>
		new Weavie.Linux.Portal.Session(portal.Connection, Destination, sessionHandle).CloseAsync();

	private static string Token(string operation) => $"weavie_{operation}_{Guid.NewGuid():N}";

	private static IReadOnlySet<string> ReadBoundShortcutIds(
		IReadOnlyDictionary<string, VariantValue> results,
		IReadOnlySet<string> requested) {
		if (!results.TryGetValue("shortcuts", out var shortcuts)
			|| shortcuts.Type != VariantValueType.Array) {
			throw new InvalidOperationException("The global-shortcuts portal returned no bound-shortcut list.");
		}

		var bound = new HashSet<string>(StringComparer.Ordinal);
		for (int index = 0; index < shortcuts.Count; index++) {
			var entry = shortcuts.GetItem(index);
			if (entry.Type != VariantValueType.Struct
				|| entry.Count != 2
				|| entry.GetItem(0).Type != VariantValueType.String) {
				throw new InvalidOperationException("The global-shortcuts portal returned a malformed shortcut entry.");
			}
			string id = entry.GetItem(0).GetString();
			if (!requested.Contains(id)) {
				throw new InvalidOperationException($"The global-shortcuts portal returned unrequested shortcut '{id}'.");
			}
			bound.Add(id);
		}
		return bound;
	}

	private static void RequireSuccess(uint response, string operation) {
		if (response != 0) {
			throw new InvalidOperationException(
				response == 1
					? $"The desktop declined permission to {operation}."
					: $"The desktop could not {operation} (portal response {response}).");
		}
	}

	private sealed class PortalConnection(DBusConnection connection, GlobalShortcuts shortcuts) : IDisposable {
		internal DBusConnection Connection { get; } = connection;
		internal GlobalShortcuts Shortcuts { get; } = shortcuts;
		internal IDisposable? ActivationSubscription { get; set; }

		public void Dispose() {
			ActivationSubscription?.Dispose();
			Connection.Dispose();
		}
	}

	private sealed class SessionWatch(string sessionHandle) : IDisposable {
		internal string SessionHandle { get; } = sessionHandle;
		internal IDisposable? Subscription { get; set; }

		public void Dispose() => Subscription?.Dispose();
	}
}
