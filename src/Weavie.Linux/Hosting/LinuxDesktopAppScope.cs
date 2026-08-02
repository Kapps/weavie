using System.Text.RegularExpressions;
using Tmds.DBus.Protocol;
using Weavie.Linux.Systemd;

namespace Weavie.Linux.Hosting;

internal sealed partial class LinuxDesktopAppScope : ILinuxDesktopAppScope {
	private const string Destination = "org.freedesktop.systemd1";
	private const string ManagerPath = "/org/freedesktop/systemd1";
	private const string UnitInterface = "org.freedesktop.systemd1.Unit";

	public async Task EnsureAsync(CancellationToken ct) {
		string address = DBusAddress.Session
			?? throw new InvalidOperationException("The desktop session has no D-Bus session bus.");
		using var connection = new DBusConnection(address);
		await connection.ConnectAsync().ConfigureAwait(false);
		ct.ThrowIfCancellationRequested();

		var manager = new Manager(connection, Destination, ManagerPath);
		string currentUnit = await GetCurrentUnitAsync(connection, manager).ConfigureAwait(false);
		if (IsAppUnit(currentUnit)) {
			return;
		}

		string scope = $"app-weavie-{LinuxDesktopIdentity.AppId}-{Guid.NewGuid():N}.scope";
		await AwaitScopeCreationAsync(
			scope,
			manager.SubscribeAsync,
			(onRemoved, onError) => manager.WatchJobRemovedAsync(
				notification => {
					if (!notification.HasValue) {
						onError(notification.Exception);
					} else {
						onRemoved(notification.Value.Unit, notification.Value.Result);
					}
				},
				ObserverFlags.EmitAll,
				emitOnCapturedContext: false,
				state: null),
			async () => _ = await manager.StartTransientUnitAsync(
				scope,
				"fail",
				[
					("Description", "Weavie"),
					("PIDs", VariantValue.Array(new uint[] { (uint)Environment.ProcessId })),
					("Slice", "app.slice"),
				],
				[]).ConfigureAwait(false),
			ct).ConfigureAwait(false);

		currentUnit = await GetCurrentUnitAsync(connection, manager).ConfigureAwait(false);
		if (!string.Equals(currentUnit, scope, StringComparison.Ordinal)) {
			throw new InvalidOperationException(
				$"systemd created '{scope}', but Weavie remained in application unit '{currentUnit}'.");
		}
	}

	internal static async Task AwaitScopeCreationAsync(
		string scope,
		Func<Task> subscribe,
		Func<Action<string, string>, Action<Exception>, ValueTask<IDisposable>> watchJobRemoved,
		Func<Task> start,
		CancellationToken ct) {
		ct.ThrowIfCancellationRequested();
		await subscribe().ConfigureAwait(false);
		ct.ThrowIfCancellationRequested();
		var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var subscription = await watchJobRemoved(
			(unit, result) => {
				if (string.Equals(unit, scope, StringComparison.Ordinal)) {
					completion.TrySetResult(result);
				}
			},
			error => completion.TrySetException(error)).ConfigureAwait(false);
		using var cancellation = ct.Register(() => completion.TrySetCanceled(ct));
		ct.ThrowIfCancellationRequested();
		await start().ConfigureAwait(false);

		string result = await completion.Task.ConfigureAwait(false);
		if (!string.Equals(result, "done", StringComparison.Ordinal)) {
			throw new InvalidOperationException($"systemd could not create the Weavie application scope ({result}).");
		}
	}

	internal static bool IsAppUnit(string unit) => AppUnitPattern().IsMatch(unit);

	private static async Task<string> GetCurrentUnitAsync(DBusConnection connection, Manager manager) {
		var unitPath = await manager.GetUnitByPIDAsync((uint)Environment.ProcessId).ConfigureAwait(false);
		var properties = new Properties(connection, Destination, unitPath);
		var id = await properties.GetAsync(UnitInterface, "Id").ConfigureAwait(false);
		if (id.Type != VariantValueType.String) {
			throw new InvalidOperationException("systemd returned a non-string application unit identifier.");
		}
		return id.GetString();
	}

	[GeneratedRegex(
		@"^app-(?:[A-Za-z0-9]+-)?io\.github\.kapps\.weavie(?:-[A-Za-z0-9]+\.scope|(?:@[A-Za-z0-9]*|-autostart)?\.service)$",
		RegexOptions.CultureInvariant)]
	private static partial Regex AppUnitPattern();
}
