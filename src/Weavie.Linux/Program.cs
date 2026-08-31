using Weavie.Core;
using Weavie.Hosting.Desktop;
using Weavie.Linux;
using Weavie.Linux.Hosting;
using Weavie.Linux.Native;

// Before any GTK or graphics setup: a launch that only hands paths to the running instance must cost
// nothing and exit, not boot a second app behind the first one's window.
var launch = LaunchArguments.Parse(args);
// Blocking, not awaited: an async entry point resumes on the thread pool, and everything below — gtk_init,
// the web view, the main loop — must run on the process main thread.
if (launch.Paths.Count > 0
	&& InstanceClient.OfferAsync(WeaviePaths.Root, launch.Paths, CancellationToken.None)
		.GetAwaiter().GetResult().Accepted) {
	return 0;
}

LinuxGraphicsCompatibility.Apply();
// Before GTK: the display-sync library only wins symbol resolution while libdrm is still unloaded.
DisplaySync.Load();
GLib.g_set_prgname(LinuxDesktopIdentity.AppId);
LinuxDesktopIdentity.EnsureInstalled();
Gtk.gtk_init();
DisplaySync.TrackMonitors();

const string missingAudioSink =
	"Weavie may freeze or crash because GStreamer's 'autoaudiosink' element is missing. "
	+ "Install GStreamer Good Plug-ins, then restart Weavie.\n\n"
	+ "Debian/Ubuntu: gstreamer1.0-plugins-good\nFedora: gstreamer1-plugins-good\nArch: gst-plugins-good";
if (!GStreamer.HasAutoAudioSink()) {
	IntPtr dialog = Gtk.gtk_alert_dialog_new(IntPtr.Zero);
	Gtk.gtk_alert_dialog_set_message(dialog, missingAudioSink);
	Gtk.gtk_alert_dialog_set_modal(dialog, true);
	_ = MainLoopWait.For(
		callback => Gtk.gtk_alert_dialog_choose(dialog, IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero),
		result => {
			int button = Gtk.gtk_alert_dialog_choose_finish(dialog, result, out IntPtr error);
			GLib.g_clear_error(ref error);
			return button;
		});
	GLib.g_object_unref(dialog);
}

var host = new WorkspaceHost();
host.SetLaunchPaths(launch.Paths);
host.Start();
GtkMain.Run();
host.Shutdown();
return 0;
