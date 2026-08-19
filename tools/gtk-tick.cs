// Measures how fast GTK3's frame clock ticks on this machine, with no WebKit involved.
// Run with: dotnet run tools/gtk-tick.cs
//
// WebKitGTK 4.1 paints through a GTK3 widget, so its presentation can never outrun this clock. GTK3 takes the
// interval from presentation timings when it gets them and otherwise falls back to a hardcoded 16667us — so a
// result of exactly 60Hz on a faster display means the fallback is in play and no WebKit setting can beat it.
#:property AllowUnsafeBlocks=true

using System.Runtime.InteropServices;

internal partial class GtkTick {
	private const string Gtk = "libgtk-3.so.0";
	private const int Seconds = 3;

	[LibraryImport(Gtk)]
	private static partial void gtk_init(IntPtr argc, IntPtr argv);

	[LibraryImport(Gtk)]
	private static partial IntPtr gtk_window_new(int type);

	[LibraryImport(Gtk)]
	private static partial void gtk_window_set_default_size(IntPtr window, int width, int height);

	[LibraryImport(Gtk)]
	private static partial IntPtr gtk_drawing_area_new();

	[LibraryImport(Gtk)]
	private static partial void gtk_container_add(IntPtr container, IntPtr child);

	[LibraryImport(Gtk)]
	private static partial void gtk_widget_show_all(IntPtr widget);

	[LibraryImport(Gtk)]
	private static partial void gtk_widget_queue_draw(IntPtr widget);

	[LibraryImport(Gtk)]
	private static partial uint gtk_widget_add_tick_callback(
		IntPtr widget, IntPtr callback, IntPtr data, IntPtr notify);

	[LibraryImport(Gtk)]
	private static partial void gtk_main();

	[LibraryImport(Gtk)]
	private static partial void gtk_main_quit();

	[LibraryImport("libgdk-3.so.0")]
	private static partial long gdk_frame_clock_get_frame_time(IntPtr frameClock);

	[LibraryImport("libgdk-3.so.0")]
	private static partial IntPtr gdk_display_get_default();

	[LibraryImport("libgdk-3.so.0")]
	private static partial IntPtr gdk_display_get_monitor(IntPtr display, int index);

	[LibraryImport("libgdk-3.so.0")]
	private static partial int gdk_monitor_get_refresh_rate(IntPtr monitor);

	private delegate int TickCallback(IntPtr widget, IntPtr frameClock, IntPtr data);

	private static readonly List<long> Frames = [];
	private static TickCallback? tick;
	private static long first;

	private static void Main() {
		gtk_init(IntPtr.Zero, IntPtr.Zero);
		int milliHz = gdk_monitor_get_refresh_rate(gdk_display_get_monitor(gdk_display_get_default(), 0));
		Console.WriteLine($"gdk says monitor 0 runs at {milliHz / 1000.0:0.###} Hz");
		Console.WriteLine($"measuring GTK3's frame clock for {Seconds}s — leave the window visible...");

		IntPtr window = gtk_window_new(0);
		gtk_window_set_default_size(window, 900, 600);
		IntPtr area = gtk_drawing_area_new();
		gtk_container_add(window, area);
		gtk_widget_show_all(window);

		tick = OnTick;
		gtk_widget_add_tick_callback(area, Marshal.GetFunctionPointerForDelegate(tick), IntPtr.Zero, IntPtr.Zero);
		gtk_main();

		List<double> intervals = [];
		for (int i = 1; i < Frames.Count; i++) {
			intervals.Add((Frames[i] - Frames[i - 1]) / 1000.0);
		}

		intervals.Sort();
		string shape = string.Join(", ", intervals
			.GroupBy(value => Math.Round(value))
			.OrderByDescending(group => group.Count())
			.Take(3)
			.Select(group => $"{group.Key}ms x{group.Count()}"));
		Console.WriteLine($"{Frames.Count} ticks in {Seconds}s = {(double)Frames.Count / Seconds:0.#} Hz"
			+ $" | p50 {intervals[intervals.Count / 2]:0.00}ms | intervals: {shape}");
	}

	private static int OnTick(IntPtr widget, IntPtr frameClock, IntPtr data) {
		long now = gdk_frame_clock_get_frame_time(frameClock);
		if (first == 0) {
			first = now;
		}

		Frames.Add(now);
		// Keep real content changing, so the compositor has a reason to present every tick.
		gtk_widget_queue_draw(widget);
		if (now - first >= Seconds * 1_000_000L) {
			gtk_main_quit();
			return 0;
		}

		return 1;
	}
}
