using Tmds.DBus.Protocol;

namespace Weavie.Linux.Hosting;

internal sealed class PortalHostIdentity(ILinuxDesktopAppScope appScope) {
	private readonly ILinuxDesktopAppScope _appScope = appScope
		?? throw new ArgumentNullException(nameof(appScope));

	internal async Task RegisterAsync(
		Func<Task> register,
		Action<string> log,
		CancellationToken ct) {
		try {
			await register().ConfigureAwait(false);
		} catch (DBusErrorReplyException ex) when (RegistryIsUnavailable(ex.ErrorName)) {
			try {
				await _appScope.EnsureAsync(ct).ConfigureAwait(false);
			} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
				throw;
			} catch (Exception scopeError) {
				throw new InvalidOperationException(
					"This desktop portal cannot register host applications, and Weavie could not enter its systemd desktop-app scope.",
					scopeError);
			}
			log(
				$"[hotkey] host portal identity registration is unavailable ({ex.ErrorName}); using the systemd desktop-app identity.");
		} catch (DBusErrorReplyException ex) when (UsesDetectedSandboxIdentity(ex.ErrorName)) {
			log("[hotkey] the sandbox supplies Weavie's desktop portal identity.");
		}
	}

	internal static bool CanUseDetectedIdentity(string errorName) =>
		RegistryIsUnavailable(errorName) || UsesDetectedSandboxIdentity(errorName);

	private static bool RegistryIsUnavailable(string errorName) =>
		errorName is "org.freedesktop.DBus.Error.UnknownInterface"
			or "org.freedesktop.DBus.Error.UnknownMethod";

	private static bool UsesDetectedSandboxIdentity(string errorName) =>
		errorName is "org.freedesktop.portal.Error.NotAllowed";
}
