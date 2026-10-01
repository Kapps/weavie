using System.Runtime.InteropServices;
using Weavie.Linux.Hosting;
using Weavie.Linux.Native;

namespace Weavie.NativeBridge.Tests;

internal static partial class NativeProbe {
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void LoadChanged(IntPtr view, int loadEvent, IntPtr data);

	[LibraryImport("libwebkitgtk-6.0.so.4")]
	private static partial void webkit_settings_set_javascript_can_open_windows_automatically(IntPtr settings, [MarshalAs(UnmanagedType.Bool)] bool enabled);

	internal static int Run(Probe probe) {
		Gtk.gtk_init();
		IntPtr window = Gtk.gtk_window_new();
		IntPtr view = WebKit.webkit_web_view_new();
		Gtk.gtk_window_set_child(window, view);
		Gtk.gtk_window_present(window);
		IntPtr manager = WebKit.webkit_web_view_get_user_content_manager(view);
		webkit_settings_set_javascript_can_open_windows_automatically(WebKit.webkit_web_view_get_settings(view), true);
		var bridge = new HostBridge();
		bridge.RegisterOn(manager);
		bridge.Attach(view);
		PolicyDecisionCallback allowed = (_, _, type, _) => {
			if (probe.Started && type == (probe.Popup ? 1 : 2)) probe.Fail("The native denial handler allowed the forbidden action");
			return 0;
		};
		GLib.g_signal_connect_data(view, "decide-policy", Marshal.GetFunctionPointerForDelegate(allowed), IntPtr.Zero, IntPtr.Zero, 0);
		bridge.MessageReceived += (_, message) => probe.Receive(message);
		bridge.PolicyDenied += type => {
			if (type != (probe.Popup ? 1 : 2)) {
				probe.Fail("Unexpected native policy decision type: " + type);
				return;
			}
			probe.Denied();
		};
		LoadChanged loaded = (_, loadEvent, _) => {
			if (loadEvent == 3) probe.Loaded();
		};
		GLib.g_signal_connect_data(view, "load-changed", Marshal.GetFunctionPointerForDelegate(loaded), IntPtr.Zero, IntPtr.Zero, 0);
		probe.Execute += script => GtkMain.Invoke(() => WebKit.webkit_web_view_evaluate_javascript(
			view, script, -1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
		probe.Navigate += url => GtkMain.Invoke(() => WebKit.webkit_web_view_load_uri(view, url));
		bridge.Security.LoadAsync(probe.AppUrl, string.Empty, "this.webkit.messageHandlers.weavie.postMessage.bind(this.webkit.messageHandlers.weavie)",
			script => {
				IntPtr userScript = WebKit.webkit_user_script_new(script, WebKit.InjectTopFrame, WebKit.InjectAtDocumentStart, IntPtr.Zero, IntPtr.Zero);
				WebKit.webkit_user_content_manager_add_script(manager, userScript);
				WebKit.webkit_user_script_unref(userScript);
				return Task.CompletedTask;
			}, url => WebKit.webkit_web_view_load_uri(view, url)).GetAwaiter().GetResult();
		GtkMain.Run();
		GC.KeepAlive(loaded);
		GC.KeepAlive(allowed);
		GC.KeepAlive(bridge);
		return probe.ExitCode;
	}
}
