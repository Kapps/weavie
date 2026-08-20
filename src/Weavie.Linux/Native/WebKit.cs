using System.Runtime.InteropServices;

namespace Weavie.Linux.Native;

/// <summary>
/// P/Invoke into WebKitGTK 6.0 (the GTK 4 API) and its JavaScriptCore-GTK companion — web view,
/// user-content manager, the custom <c>app://</c> scheme, and outbound <c>evaluateJavaScript</c>.
/// </summary>
internal static partial class WebKit {
	private const string Lib = "libwebkitgtk-6.0.so.4";
	private const string Jsc = "libjavascriptcoregtk-6.0.so.1";
	private const string PreferPageRenderingUpdatesNear60Fps = "PreferPageRenderingUpdatesNear60FPS";

	/// <summary><c>WEBKIT_USER_CONTENT_INJECT_TOP_FRAME</c> — inject user scripts into the top frame only.</summary>
	internal const int InjectTopFrame = 1;

	/// <summary><c>WEBKIT_USER_SCRIPT_INJECT_AT_DOCUMENT_START</c> — run user scripts before the page loads.</summary>
	internal const int InjectAtDocumentStart = 0;

	[LibraryImport(Lib)]
	internal static partial IntPtr webkit_web_context_get_default();

	[LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial void webkit_web_context_register_uri_scheme(
		IntPtr context, string scheme, IntPtr callback, IntPtr userData, IntPtr destroyNotify);

	/// <summary>Registers a script-message channel in the page's main world (a NULL world name).</summary>
	[LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool webkit_user_content_manager_register_script_message_handler(
		IntPtr manager, string name, IntPtr worldName);

	[LibraryImport(Lib)]
	internal static partial void webkit_user_content_manager_add_script(IntPtr manager, IntPtr script);

	[LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial IntPtr webkit_user_script_new(
		string source, int injectedFrames, int injectionTime, IntPtr allowList, IntPtr blockList);

	[LibraryImport(Lib)]
	internal static partial IntPtr webkit_web_view_new();

	/// <summary>The view's own user-content manager — the one script messages and injected scripts go through.</summary>
	[LibraryImport(Lib)]
	internal static partial IntPtr webkit_web_view_get_user_content_manager(IntPtr webView);

	[LibraryImport(Lib)]
	internal static partial IntPtr webkit_web_view_get_settings(IntPtr webView);

	[LibraryImport(Lib)]
	private static partial IntPtr webkit_settings_get_all_features();

	[LibraryImport(Lib)]
	private static partial nuint webkit_feature_list_get_length(IntPtr features);

	[LibraryImport(Lib)]
	private static partial IntPtr webkit_feature_list_get(IntPtr features, nuint index);

	[LibraryImport(Lib)]
	private static partial IntPtr webkit_feature_get_identifier(IntPtr feature);

	[LibraryImport(Lib)]
	private static partial void webkit_settings_set_feature_enabled(
		IntPtr settings, IntPtr feature, [MarshalAs(UnmanagedType.Bool)] bool enabled);

	[LibraryImport(Lib)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool webkit_settings_get_feature_enabled(IntPtr settings, IntPtr feature);

	[LibraryImport(Lib)]
	private static partial void webkit_feature_list_unref(IntPtr features);

	/// <summary>Lets rendering updates follow the display's native refresh rate instead of WebKit's 60fps preference.</summary>
	internal static void EnableNativeRefreshRate(IntPtr settings) {
		IntPtr features = webkit_settings_get_all_features();
		try {
			for (nuint index = 0; index < webkit_feature_list_get_length(features); index++) {
				IntPtr feature = webkit_feature_list_get(features, index);
				if (Marshal.PtrToStringUTF8(webkit_feature_get_identifier(feature)) == PreferPageRenderingUpdatesNear60Fps) {
					webkit_settings_set_feature_enabled(settings, feature, false);
					if (webkit_settings_get_feature_enabled(settings, feature))
						throw new InvalidOperationException("WebKit refused native-refresh rendering updates.");
					return;
				}
			}
		} finally {
			webkit_feature_list_unref(features);
		}

		throw new InvalidOperationException($"WebKit feature not found: {PreferPageRenderingUpdatesNear60Fps}");
	}

	[LibraryImport(Lib)]
	internal static partial void webkit_settings_set_enable_developer_extras(
		IntPtr settings, [MarshalAs(UnmanagedType.Bool)] bool enabled);

	[LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial void webkit_web_view_load_uri(IntPtr webView, string uri);

	[LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial void webkit_web_view_load_html(IntPtr webView, string content, IntPtr baseUri);

	/// <summary>Drops every user script registered on the manager — clears the welcome injection before the workspace loads.</summary>
	[LibraryImport(Lib)]
	internal static partial void webkit_user_content_manager_remove_all_scripts(IntPtr manager);

	[LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial void webkit_web_view_evaluate_javascript(
		IntPtr webView, string script, nint length, IntPtr worldName, IntPtr sourceUri,
		IntPtr cancellable, IntPtr callback, IntPtr userData);

	[LibraryImport(Lib)]
	internal static partial IntPtr webkit_uri_scheme_request_get_path(IntPtr request);

	[LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial void webkit_uri_scheme_request_finish(
		IntPtr request, IntPtr stream, long streamLength, string contentType);

	[LibraryImport(Jsc)]
	internal static partial IntPtr jsc_value_to_string(IntPtr value);
}
