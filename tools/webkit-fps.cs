// Measures the frame rate a bare WebKitGTK window reaches on this machine, isolating the engine from Weavie.
// Run with: dotnet run tools/webkit-fps.cs [--60fps-pref]
//
// Weavie disables WebKit's PreferPageRenderingUpdatesNear60FPS feature so rendering follows the display's
// refresh rate; this reproduces that by default. Pass --60fps-pref to leave the feature on for comparison.
// To see what the fallback costs, set WEBKIT_FORCE_VBLANK_TIMER=1 in the environment before running.
#:property AllowUnsafeBlocks=true

using System.Runtime.InteropServices;

internal partial class WebKitFps {
	private const string Gtk = "libgtk-3.so.0";
	private const string Wk = "libwebkit2gtk-4.1.so.0";
	private const string Feature = "PreferPageRenderingUpdatesNear60FPS";

	private const string Page = """
		<!doctype html><html><head><meta charset=utf-8></head>
		<body style="margin:0;background:#111">
		<div id=box style="width:200px;height:200px;background:#0f0"></div>
		<script>
		const intervals = [];
		let last = performance.now();
		const end = last + 3000;
		const box = document.getElementById('box');
		const tick = (now) => {
			intervals.push(now - last);
			last = now;
			// Keep a compositor-driven property changing so the page always has something to present.
			box.style.transform = `translateX(${(now / 8) % 200}px)`;
			if (now < end) { requestAnimationFrame(tick); return; }
			const sorted = intervals.slice(1).sort((a, b) => a - b);
			const p50 = sorted[sorted.length >> 1];
			document.title = `FPS ${intervals.length} frames in 3s = ${(intervals.length / 3).toFixed(0)} Hz`
				+ ` | p50 ${p50.toFixed(2)}ms = ${(1000 / p50).toFixed(0)} Hz`
				+ ` | p95 ${sorted[(sorted.length * 0.95) | 0].toFixed(2)}ms`;
		};
		requestAnimationFrame(tick);
		</script></body></html>
		""";

	[LibraryImport(Gtk)]
	private static partial void gtk_init(IntPtr argc, IntPtr argv);

	[LibraryImport(Gtk)]
	private static partial IntPtr gtk_window_new(int type);

	[LibraryImport(Gtk)]
	private static partial void gtk_window_set_default_size(IntPtr window, int width, int height);

	[LibraryImport(Gtk)]
	private static partial void gtk_container_add(IntPtr container, IntPtr child);

	[LibraryImport(Gtk)]
	private static partial void gtk_widget_show_all(IntPtr widget);

	[LibraryImport(Gtk)]
	private static partial void gtk_main();

	[LibraryImport(Gtk)]
	private static partial void gtk_main_quit();

	[LibraryImport(Wk)]
	private static partial IntPtr webkit_web_view_new();

	[LibraryImport(Wk)]
	private static partial IntPtr webkit_web_view_get_settings(IntPtr webView);

	[LibraryImport(Wk, StringMarshalling = StringMarshalling.Utf8)]
	private static partial void webkit_web_view_load_html(IntPtr webView, string content, IntPtr baseUri);

	[LibraryImport(Wk)]
	private static partial IntPtr webkit_web_view_get_title(IntPtr webView);

	[LibraryImport(Wk)]
	private static partial IntPtr webkit_settings_get_all_features();

	[LibraryImport(Wk)]
	private static partial nuint webkit_feature_list_get_length(IntPtr features);

	[LibraryImport(Wk)]
	private static partial IntPtr webkit_feature_list_get(IntPtr features, nuint index);

	[LibraryImport(Wk)]
	private static partial IntPtr webkit_feature_get_identifier(IntPtr feature);

	[LibraryImport(Wk)]
	private static partial void webkit_settings_set_feature_enabled(
		IntPtr settings, IntPtr feature, [MarshalAs(UnmanagedType.Bool)] bool enabled);

	[LibraryImport("libglib-2.0.so.0")]
	private static partial uint g_timeout_add(uint interval, IntPtr function, IntPtr data);

	private delegate int SourceFunc(IntPtr data);

	private static IntPtr view;
	private static SourceFunc? poll;

	private static void Main(string[] args) {
		bool keep60 = args.Contains("--60fps-pref");
		gtk_init(IntPtr.Zero, IntPtr.Zero);
		IntPtr window = gtk_window_new(0);
		gtk_window_set_default_size(window, 900, 600);
		view = webkit_web_view_new();

		if (!keep60) {
			DisableNear60FpsPreference(webkit_web_view_get_settings(view));
		}

		Console.WriteLine($"PreferPageRenderingUpdatesNear60FPS: {(keep60 ? "left enabled" : "disabled (as Weavie does)")}"
			+ $"   vblank timer forced: {Environment.GetEnvironmentVariable("WEBKIT_FORCE_VBLANK_TIMER") ?? "no"}");
		Console.WriteLine("measuring for 3s — leave the window visible and unobstructed...");

		gtk_container_add(window, view);
		webkit_web_view_load_html(view, Page, IntPtr.Zero);
		gtk_widget_show_all(window);

		poll = ReadTitle;
		g_timeout_add(250, Marshal.GetFunctionPointerForDelegate(poll), IntPtr.Zero);
		gtk_main();
	}

	private static int ReadTitle(IntPtr data) {
		string title = Marshal.PtrToStringUTF8(webkit_web_view_get_title(view)) ?? string.Empty;
		if (!title.StartsWith("FPS", StringComparison.Ordinal)) {
			return 1;
		}

		Console.WriteLine(title);
		gtk_main_quit();
		return 0;
	}

	private static void DisableNear60FpsPreference(IntPtr settings) {
		IntPtr features = webkit_settings_get_all_features();
		for (nuint i = 0; i < webkit_feature_list_get_length(features); i++) {
			IntPtr feature = webkit_feature_list_get(features, i);
			if (Marshal.PtrToStringUTF8(webkit_feature_get_identifier(feature)) == Feature) {
				webkit_settings_set_feature_enabled(settings, feature, false);
				return;
			}
		}

		Console.WriteLine($"warning: WebKit feature not found: {Feature}");
	}
}
