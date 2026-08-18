// Measures the frame rate WebKit's GTK4 build (webkitgtk-6.0) reaches, against tools/webkit-fps.cs (GTK3).
// Run with: dotnet run tools/webkit6-fps.cs [--60fps-pref]
//
// This is the question a port answers: WebKitGTK 4.1 presents through GTK3's AcceleratedBackingStore, while
// 6.0 hands GdkTextures to GSK — a different path entirely. Needs libwebkitgtk-6.0-4 installed. GSK_RENDERER
// (ngl/gl/vulkan/cairo) selects how GTK4 draws, so it is worth running across those values.
#:property AllowUnsafeBlocks=true

using System.Runtime.InteropServices;

internal partial class WebKit6Fps {
	private const string Gtk = "libgtk-4.so.1";
	private const string Wk = "libwebkitgtk-6.0.so.4";
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
			box.style.transform = `translateX(${(now / 8) % 200}px)`;
			if (now < end) { requestAnimationFrame(tick); return; }
			const sorted = intervals.slice(1).sort((a, b) => a - b);
			const counts = new Map();
			for (const value of sorted) {
				const ms = Math.round(value);
				counts.set(ms, (counts.get(ms) ?? 0) + 1);
			}
			const shape = [...counts.entries()]
				.sort((a, b) => b[1] - a[1]).slice(0, 3)
				.map(([ms, n]) => `${ms}ms x${n}`).join(", ");
			document.title = `FPS ${intervals.length} frames in 3s = ${(intervals.length / 3).toFixed(0)} Hz`
				+ ` | p50 ${sorted[sorted.length >> 1].toFixed(2)}ms | intervals: ${shape}`;
		};
		requestAnimationFrame(tick);
		</script></body></html>
		""";

	[LibraryImport(Gtk)]
	private static partial void gtk_init();

	[LibraryImport(Gtk)]
	private static partial IntPtr gtk_window_new();

	[LibraryImport(Gtk)]
	private static partial void gtk_window_set_default_size(IntPtr window, int width, int height);

	[LibraryImport(Gtk)]
	private static partial void gtk_window_set_child(IntPtr window, IntPtr child);

	[LibraryImport(Gtk)]
	private static partial void gtk_window_present(IntPtr window);

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

	[LibraryImport("libglib-2.0.so.0")]
	private static partial IntPtr g_main_loop_new(IntPtr context, [MarshalAs(UnmanagedType.Bool)] bool running);

	[LibraryImport("libglib-2.0.so.0")]
	private static partial void g_main_loop_run(IntPtr loop);

	[LibraryImport("libglib-2.0.so.0")]
	private static partial void g_main_loop_quit(IntPtr loop);

	private delegate int SourceFunc(IntPtr data);

	private static IntPtr view;
	private static IntPtr loop;
	private static SourceFunc? poll;

	private static void Main(string[] args) {
		bool keep60 = args.Contains("--60fps-pref");
		gtk_init();
		IntPtr window = gtk_window_new();
		gtk_window_set_default_size(window, 900, 600);
		view = webkit_web_view_new();

		if (!keep60) {
			DisableNear60FpsPreference(webkit_web_view_get_settings(view));
		}

		Console.WriteLine($"webkitgtk-6.0 (GTK4)   PreferPageRenderingUpdatesNear60FPS:"
			+ $" {(keep60 ? "left enabled" : "disabled (as Weavie does)")}"
			+ $"   GSK_RENDERER: {Environment.GetEnvironmentVariable("GSK_RENDERER") ?? "(default)"}");
		Console.WriteLine("measuring for 3s — leave the window visible and unobstructed...");

		gtk_window_set_child(window, view);
		webkit_web_view_load_html(view, Page, IntPtr.Zero);
		gtk_window_present(window);

		poll = ReadTitle;
		g_timeout_add(250, Marshal.GetFunctionPointerForDelegate(poll), IntPtr.Zero);
		loop = g_main_loop_new(IntPtr.Zero, false);
		g_main_loop_run(loop);
	}

	private static int ReadTitle(IntPtr data) {
		string title = Marshal.PtrToStringUTF8(webkit_web_view_get_title(view)) ?? string.Empty;
		if (!title.StartsWith("FPS", StringComparison.Ordinal)) {
			return 1;
		}

		Console.WriteLine(title);
		g_main_loop_quit(loop);
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
