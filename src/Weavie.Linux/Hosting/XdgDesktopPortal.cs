using Tmds.DBus.Protocol;
using Weavie.Linux.Portal;

namespace Weavie.Linux.Hosting;

internal static class XdgDesktopPortal {
	internal const string Destination = "org.freedesktop.portal.Desktop";
	internal const string Path = "/org/freedesktop/portal/desktop";

	internal static async Task RegisterIdentityAsync(DBusConnection connection, Action<string> log) {
		ArgumentNullException.ThrowIfNull(connection);
		ArgumentNullException.ThrowIfNull(log);
		var registry = new Registry(connection, Destination, Path);
		try {
			await registry.RegisterAsync(LinuxDesktopIdentity.AppId, []).ConfigureAwait(false);
		} catch (DBusErrorReplyException ex) when (ex.ErrorName == "org.freedesktop.portal.Error.NotAllowed") {
			log("the sandbox supplies Weavie's desktop portal identity.");
		} catch (DBusErrorReplyException ex) when (
			ex.ErrorName is "org.freedesktop.DBus.Error.UnknownInterface"
				or "org.freedesktop.DBus.Error.UnknownMethod") {
			log("the desktop portal is using its built-in host application identity detection.");
		}
	}
}
