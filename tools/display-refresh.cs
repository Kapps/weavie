// Reports what paces WebKitGTK's rendering updates on this machine — run with: dotnet run tools/display-refresh.cs
//
// WebKitGTK takes its rendering-update rate from gdk_monitor_get_refresh_rate() and waits on DRM vblanks.
// When it cannot open a DRM node whose connector matches the monitor's physical size, it silently falls back
// to a timer hardcoded at 60fps, pinning every surface to 60Hz regardless of the panel.
#:property AllowUnsafeBlocks=true

using System.Runtime.InteropServices;

internal partial class DisplayRefresh {
	[LibraryImport("libgtk-3.so.0")]
	private static partial void gtk_init(IntPtr argc, IntPtr argv);

	[LibraryImport("libgdk-3.so.0")]
	private static partial IntPtr gdk_display_get_default();

	[LibraryImport("libgdk-3.so.0")]
	private static partial int gdk_display_get_n_monitors(IntPtr display);

	[LibraryImport("libgdk-3.so.0")]
	private static partial IntPtr gdk_display_get_monitor(IntPtr display, int index);

	[LibraryImport("libgdk-3.so.0")]
	private static partial int gdk_monitor_get_refresh_rate(IntPtr monitor);

	[LibraryImport("libgdk-3.so.0")]
	private static partial int gdk_monitor_get_width_mm(IntPtr monitor);

	[LibraryImport("libgdk-3.so.0")]
	private static partial int gdk_monitor_get_height_mm(IntPtr monitor);

	[LibraryImport("libgdk-3.so.0")]
	private static partial IntPtr gdk_monitor_get_model(IntPtr monitor);

	private static void Main() {
		Console.WriteLine($"session: {Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "(unset)"}"
			+ $"  wayland: {Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? "(unset)"}"
			+ $"  gdk backend: {Environment.GetEnvironmentVariable("GDK_BACKEND") ?? "(default)"}");

		gtk_init(IntPtr.Zero, IntPtr.Zero);
		IntPtr display = gdk_display_get_default();
		if (display == IntPtr.Zero) {
			Console.WriteLine("no GDK display — run this from inside your desktop session");
			return;
		}

		int best = 0;
		for (int i = 0; i < gdk_display_get_n_monitors(display); i++) {
			IntPtr monitor = gdk_display_get_monitor(display, i);
			int milliHz = gdk_monitor_get_refresh_rate(monitor);
			int widthMm = gdk_monitor_get_width_mm(monitor);
			int heightMm = gdk_monitor_get_height_mm(monitor);
			best = Math.Max(best, milliHz);
			string model = Marshal.PtrToStringUTF8(gdk_monitor_get_model(monitor)) ?? "(unnamed)";
			Console.WriteLine($"monitor {i} [{model}]: gdk refresh = {milliHz / 1000.0:0.###} Hz"
				+ $"  physical = {widthMm}x{heightMm} mm"
				+ (widthMm == 0 || heightMm == 0 ? "  <-- 0mm cannot match a DRM connector" : string.Empty));
		}

		// WebKit opens the primary node read-write and matches a connector by the monitor's physical size.
		string[] nodes = Directory.Exists("/dev/dri") ? Directory.GetFiles("/dev/dri", "card*") : [];
		bool drmUsable = false;
		foreach (string node in nodes) {
			try {
				using var handle = File.Open(node, FileMode.Open, FileAccess.ReadWrite);
				drmUsable = true;
				Console.WriteLine($"{node}: opened read-write");
			} catch (Exception ex) {
				Console.WriteLine($"{node}: CANNOT OPEN ({ex.GetType().Name})");
			}
		}

		if (nodes.Length == 0) {
			Console.WriteLine("/dev/dri: no card nodes");
		}

		Console.WriteLine();
		if (!drmUsable) {
			Console.WriteLine("VERDICT: no usable DRM node -> WebKit uses its timer, hardcoded to 60fps.");
		} else if (best <= 60000) {
			Console.WriteLine($"VERDICT: GDK reports {best / 1000.0:0.###} Hz -> WebKit paces rendering at that rate.");
		} else {
			Console.WriteLine($"VERDICT: GDK reports {best / 1000.0:0.###} Hz and DRM is open -> the pipeline can pace"
				+ " above 60fps. If the app still measures 60, the vblank monitor failed to match a connector"
				+ " (run the app from a terminal and look for: Failed to create DRM vblank monitor).");
		}
	}
}
