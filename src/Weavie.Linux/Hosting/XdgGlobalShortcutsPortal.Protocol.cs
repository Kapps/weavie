using Tmds.DBus.Protocol;
using Weavie.Linux.Portal;

namespace Weavie.Linux.Hosting;

internal sealed partial class XdgGlobalShortcutsPortal {
	private async Task<(uint Response, Dictionary<string, VariantValue> Results)> RequestAsync(
		ConnectedPortal connected,
		string token,
		Func<Task<ObjectPath>> invoke,
		CancellationToken ct) {
		string sender = connected.Connection.UniqueName
			?? throw new InvalidOperationException("The D-Bus session connection has no unique name.");
		string senderPath = sender[1..].Replace(".", "_", StringComparison.Ordinal);
		var expectedHandle = new ObjectPath($"/org/freedesktop/portal/desktop/request/{senderPath}/{token}");
		var request = new Request(connected.Connection, connected.Destination, expectedHandle);
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
		using var cancellation = ct.Register(() => {
			_ = request.CloseAsync();
			response.TrySetCanceled(ct);
		});

		ct.ThrowIfCancellationRequested();
		var actualHandle = await invoke().ConfigureAwait(false);
		if (!string.Equals(actualHandle.ToString(), expectedHandle.ToString(), StringComparison.Ordinal)) {
			throw new InvalidOperationException(
				$"The portal returned request handle '{actualHandle}' instead of '{expectedHandle}'.");
		}
		return await response.Task.ConfigureAwait(false);
	}

	private static async Task CloseSessionAfterFailedBindAsync(
		ConnectedPortal connected,
		string sessionHandle) {
		try {
			await CloseSessionAsync(connected, sessionHandle).ConfigureAwait(false);
		} catch (Exception ex) when (ex is DBusOwnerChangedException or DBusConnectionException) {
		}
	}

	private static Task CloseSessionAsync(ConnectedPortal connected, string sessionHandle) =>
		new Weavie.Linux.Portal.Session(connected.Connection, connected.Destination, sessionHandle).CloseAsync();

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

	internal static bool CanUseDetectedIdentity(string errorName) =>
		PortalHostIdentity.CanUseDetectedIdentity(errorName);

	internal static bool SetupIsCurrent(
		long setupGeneration,
		long currentGeneration,
		string expectedOwner,
		string? currentOwner) =>
		setupGeneration == currentGeneration
			&& string.Equals(expectedOwner, currentOwner, StringComparison.Ordinal);

	private static void RequireSuccess(uint response, string operation) {
		if (response != 0) {
			throw new InvalidOperationException(
				response == 1
					? $"The desktop declined permission to {operation}."
					: $"The desktop could not {operation} (portal response {response}).");
		}
	}

	private sealed record ActivationWatch(DBusConnection Connection, long Generation);
	private sealed record ConnectedPortal(
		DBusConnection Connection,
		string Destination,
		GlobalShortcuts Shortcuts,
		long Generation);
	private sealed record RetiredConnection(NameOwnerWatcher? OwnerWatcher, DBusConnection Connection);

	private sealed class SessionWatch(string sessionHandle) {
		internal string SessionHandle { get; } = sessionHandle;
		internal bool Closed { get; set; }
	}
}
