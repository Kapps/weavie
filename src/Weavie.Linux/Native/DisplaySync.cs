using System.Runtime.InteropServices;

namespace Weavie.Linux.Native;

/// <summary>
/// Loads and feeds <c>libweavie-display-sync.so</c>, which repairs the display cadence WebKitGTK derives for
/// the page it renders. WebKit ticks its rendering updates from a DRM vblank monitor that only constructs when
/// a connected connector's EDID millimetres exactly equal the size the compositor reports, and only ticks when
/// the driver implements the legacy vblank ioctl; neither holds on a common Wayland desktop, and WebKit then
/// silently paces every page at a hardcoded 60fps. The library answers both questions correctly, so it has to
/// be in the global symbol scope before GTK pulls libdrm in, and has to know the monitors GDK reports before
/// the first web view is realized.
/// </summary>
internal static partial class DisplaySync {
	private const string Lib = "libweavie-display-sync.so";
	private const int RtldNow = 2;
	private const int RtldGlobal = 0x100;

	// Kept alive: GDK holds a bare function pointer to this for as long as the display exists.
	private static ItemsChangedCallback? _onMonitorsChanged;

	[LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
	private static partial IntPtr dlopen(string file, int mode);

	[LibraryImport(Lib)]
	private static partial void weavie_display_sync_add_monitor(int widthMm, int heightMm, uint refreshMilliHz);

	[LibraryImport(Lib)]
	private static partial void weavie_display_sync_clear_monitors();

	/// <summary>
	/// Puts the library in front of libdrm for this process. Must run before any GTK, GDK, or WebKit call.
	/// </summary>
	internal static void Load() {
		string path = Path.Combine(AppContext.BaseDirectory, Lib);
		IntPtr handle = dlopen(path, RtldNow | RtldGlobal);
		if (handle == IntPtr.Zero) {
			throw new InvalidOperationException($"Could not load {path}, so WebKit would render at 60fps.");
		}

		NativeLibrary.SetDllImportResolver(
			typeof(DisplaySync).Assembly, (name, _, _) => name == Lib ? handle : IntPtr.Zero);
	}

	/// <summary>Registers the monitors GDK reports, and keeps them registered across display changes.</summary>
	internal static void TrackMonitors() {
		IntPtr monitors = Gdk.gdk_display_get_monitors(Gdk.gdk_display_get_default());
		Register(monitors);
		_onMonitorsChanged = (model, _, _, _, _) => Register(model);
		_ = GLib.g_signal_connect_data(
			monitors,
			"items-changed",
			Marshal.GetFunctionPointerForDelegate(_onMonitorsChanged),
			IntPtr.Zero,
			IntPtr.Zero,
			0);
	}

	private static void Register(IntPtr monitors) {
		weavie_display_sync_clear_monitors();
		for (uint index = 0; index < GLib.g_list_model_get_n_items(monitors); index++) {
			IntPtr monitor = GLib.g_list_model_get_item(monitors, index);
			try {
				weavie_display_sync_add_monitor(
					Gdk.gdk_monitor_get_width_mm(monitor),
					Gdk.gdk_monitor_get_height_mm(monitor),
					(uint)Gdk.gdk_monitor_get_refresh_rate(monitor));
			} finally {
				GLib.g_object_unref(monitor);
			}
		}
	}
}
