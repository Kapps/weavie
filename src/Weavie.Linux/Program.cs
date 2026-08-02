using Weavie.Linux;
using Weavie.Linux.Hosting;
using Weavie.Linux.Native;

LinuxGraphicsCompatibility.Apply();
Gtk.gtk_init(IntPtr.Zero, IntPtr.Zero);

const string missingAudioSink =
	"Weavie may freeze or crash because GStreamer's 'autoaudiosink' element is missing. "
	+ "Install GStreamer Good Plug-ins, then restart Weavie.\n\n"
	+ "Debian/Ubuntu: gstreamer1.0-plugins-good\nFedora: gstreamer1-plugins-good\nArch: gst-plugins-good";
if (!GStreamer.HasAutoAudioSink()) {
	IntPtr dialog = Gtk.gtk_message_dialog_new(
		IntPtr.Zero, Gtk.DialogModal, Gtk.MessageWarning, Gtk.ButtonsOk, missingAudioSink);
	Gtk.gtk_window_set_title(dialog, "Missing Linux dependency");
	_ = Gtk.gtk_dialog_run(dialog);
	Gtk.gtk_widget_destroy(dialog);
}

var host = new WorkspaceHost();
host.Start();
Gtk.gtk_main();
host.Shutdown();
return 0;
