// Measures GTK4's frame clock on this machine, for comparison with tools/gtk-tick.cs (GTK3).
// Run with: dotnet run tools/gtk4-tick.cs
//
// Weavie's Linux host is GTK3 (webkit2gtk-4.1). If GTK3 ticks at 60Hz here while GTK4 tracks the display,
// then moving the host to GTK4 (webkitgtk-6.0) is what unlocks the panel's refresh rate — and if GTK4 also
// ticks at 60, the port would buy nothing and the cap is below both.
#:property AllowUnsafeBlocks=true

using System.Runtime.InteropServices;

internal partial class Gtk4Tick {
	private const string Gtk = "libgtk-4.so.1";
	private const int Seconds = 3;

	[LibraryImport(Gtk)]
	private static partial void gtk_init();

	[LibraryImport(Gtk)]
	private static partial IntPtr gtk_window_new();

	[LibraryImport(Gtk)]
	private static partial void gtk_window_set_default_size(IntPtr window, int width, int height);

	[LibraryImport(Gtk)]
	private static partial IntPtr gtk_drawing_area_new();

	[LibraryImport(Gtk)]
	private static partial void gtk_window_set_child(IntPtr window, IntPtr child);

	[LibraryImport(Gtk)]
	private static partial void gtk_window_present(IntPtr window);

	[LibraryImport(Gtk)]
	private static partial void gtk_widget_queue_draw(IntPtr widget);

	[LibraryImport(Gtk)]
	private static partial uint gtk_widget_add_tick_callback(
		IntPtr widget, IntPtr callback, IntPtr data, IntPtr notify);

	[LibraryImport(Gtk)]
	private static partial long gdk_frame_clock_get_frame_time(IntPtr frameClock);

	[LibraryImport("libglib-2.0.so.0")]
	private static partial IntPtr g_main_loop_new(IntPtr context, [MarshalAs(UnmanagedType.Bool)] bool running);

	[LibraryImport("libglib-2.0.so.0")]
	private static partial void g_main_loop_run(IntPtr loop);

	[LibraryImport("libglib-2.0.so.0")]
	private static partial void g_main_loop_quit(IntPtr loop);

	private delegate int TickCallback(IntPtr widget, IntPtr frameClock, IntPtr data);

	private static readonly List<long> Frames = [];
	private static TickCallback? tick;
	private static IntPtr loop;
	private static long first;

	private static void Main() {
		gtk_init();
		Console.WriteLine($"measuring GTK4's frame clock for {Seconds}s — leave the window visible...");

		IntPtr window = gtk_window_new();
		gtk_window_set_default_size(window, 900, 600);
		IntPtr area = gtk_drawing_area_new();
		gtk_window_set_child(window, area);
		gtk_window_present(window);

		tick = OnTick;
		gtk_widget_add_tick_callback(area, Marshal.GetFunctionPointerForDelegate(tick), IntPtr.Zero, IntPtr.Zero);
		loop = g_main_loop_new(IntPtr.Zero, false);
		g_main_loop_run(loop);

		List<double> intervals = [];
		for (int i = 1; i < Frames.Count; i++) {
			intervals.Add((Frames[i] - Frames[i - 1]) / 1000.0);
		}

		intervals.Sort();
		string shape = string.Join(", ", intervals
			.GroupBy(value => Math.Round(value, 1))
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
		gtk_widget_queue_draw(widget);
		if (now - first >= Seconds * 1_000_000L) {
			g_main_loop_quit(loop);
			return 0;
		}

		return 1;
	}
}
