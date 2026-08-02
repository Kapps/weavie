using Weavie.Linux;
using Weavie.Linux.Native;

Gtk.gtk_init(IntPtr.Zero, IntPtr.Zero);

const string missingAudioSink =
	"Weavie needs GStreamer's 'autoaudiosink' element. Install GStreamer Good Plug-ins, then restart Weavie.\n\n"
	+ "Debian/Ubuntu: gstreamer1.0-plugins-good\nFedora: gstreamer1-plugins-good\nArch: gst-plugins-good";
if (!GStreamer.HasAutoAudioSink()) {
	IntPtr dialog = Gtk.gtk_message_dialog_new(
		IntPtr.Zero, Gtk.DialogModal, Gtk.MessageError, Gtk.ButtonsClose, missingAudioSink);
	Gtk.gtk_window_set_title(dialog, "Missing Linux dependency");
	_ = Gtk.gtk_dialog_run(dialog);
	Gtk.gtk_widget_destroy(dialog);
	return 1;
}

var host = new WorkspaceHost();
host.Start();
Gtk.gtk_main();
host.Shutdown();
return 0;
