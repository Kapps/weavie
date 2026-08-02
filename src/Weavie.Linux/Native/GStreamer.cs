using System.Runtime.InteropServices;

namespace Weavie.Linux.Native;

/// <summary>Queries the GStreamer registry WebKitGTK uses for audio playback.</summary>
internal static partial class GStreamer {
	private const string Lib = "libgstreamer-1.0.so.0";

	/// <summary>True when WebKitGTK's required automatic audio output element is registered.</summary>
	internal static bool HasAutoAudioSink() {
		gst_init(IntPtr.Zero, IntPtr.Zero);
		IntPtr factory = gst_element_factory_find("autoaudiosink");
		if (factory == IntPtr.Zero) {
			return false;
		}

		GLib.g_object_unref(factory);
		return true;
	}

	[LibraryImport(Lib)]
	private static partial void gst_init(IntPtr argc, IntPtr argv);

	[LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
	private static partial IntPtr gst_element_factory_find(string name);
}
