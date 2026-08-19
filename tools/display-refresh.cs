// Reports what paces WebKitGTK's rendering updates on this machine — run with: dotnet run tools/display-refresh.cs
//
// WebKitGTK takes its rendering-update rate from gdk_monitor_get_refresh_rate() and waits on DRM vblanks.
// When it cannot open a DRM node whose connector matches the monitor's physical size, it silently falls back
// to a timer hardcoded at 60fps, pinning every surface to 60Hz regardless of the panel.
#:property AllowUnsafeBlocks=true

using System.Runtime.InteropServices;

internal partial class DisplayRefresh {
	private const string DrmSysfs = "/sys/class/drm";

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
		List<(int Width, int Height)> gdkSizes = [];
		for (int i = 0; i < gdk_display_get_n_monitors(display); i++) {
			IntPtr monitor = gdk_display_get_monitor(display, i);
			int milliHz = gdk_monitor_get_refresh_rate(monitor);
			int widthMm = gdk_monitor_get_width_mm(monitor);
			int heightMm = gdk_monitor_get_height_mm(monitor);
			best = Math.Max(best, milliHz);
			gdkSizes.Add((widthMm, heightMm));
			string model = Marshal.PtrToStringUTF8(gdk_monitor_get_model(monitor)) ?? "(unnamed)";
			Console.WriteLine($"monitor {i} [{model}]: gdk refresh = {milliHz / 1000.0:0.###} Hz"
				+ $"  physical = {widthMm}x{heightMm} mm"
				+ (widthMm == 0 || heightMm == 0 ? "  <-- 0mm cannot match a DRM connector" : string.Empty));
		}

		// The size WebKit compares against: what the kernel derived from each connected connector's EDID.
		bool matched = false;
		foreach (string connector in Directory.Exists(DrmSysfs) ? Directory.GetDirectories(DrmSysfs) : []) {
			string statusPath = Path.Combine(connector, "status");
			string edidPath = Path.Combine(connector, "edid");
			if (!File.Exists(statusPath) || File.ReadAllText(statusPath).Trim() != "connected") {
				continue;
			}

			(int Width, int Height) size = File.Exists(edidPath)
				? EdidSizeMm(File.ReadAllBytes(edidPath))
				: (0, 0);
			bool hit = gdkSizes.Contains(size);
			matched |= hit;
			Console.WriteLine($"connector {Path.GetFileName(connector)}: edid physical = {size.Width}x{size.Height} mm"
				+ (hit ? "  <-- matches a GDK monitor" : "  <-- matches no GDK monitor"));
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
		} else if (!matched) {
			Console.WriteLine($"VERDICT: GDK reports {best / 1000.0:0.###} Hz, but no connector's EDID size matches a"
				+ " GDK monitor -> WebKit cannot find the CRTC and falls back to its 60fps timer.");
		} else {
			Console.WriteLine($"VERDICT: GDK reports {best / 1000.0:0.###} Hz, DRM is open, and a connector matches"
				+ " -> nothing here explains a 60fps cap; run the app from a terminal and look for:"
				+ " Failed to create DRM vblank monitor.");
		}
	}

	// EDID carries the physical size twice: whole centimetres in the basic block, and millimetres in the first
	// detailed timing descriptor. The kernel exposes the centimetre fields x10 on the connector (proven by a
	// drmModeGetConnector trace: 700x390 against the compositor's detailed-timing 697x392), so compare those —
	// WebKit's connector match is exact equality against GDK, and that off-by-rounding is the whole failure.
	private static (int Width, int Height) EdidSizeMm(byte[] edid) {
		if (edid.Length < 128) {
			return (0, 0);
		}

		if (edid[21] > 0 && edid[22] > 0) {
			return (edid[21] * 10, edid[22] * 10);
		}

		const int detailed = 54;
		int width = ((edid[detailed + 14] >> 4) << 8) | edid[detailed + 12];
		int height = ((edid[detailed + 14] & 0x0F) << 8) | edid[detailed + 13];
		return (width, height);
	}
}
