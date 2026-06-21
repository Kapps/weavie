using System.Runtime.InteropServices;

namespace Weavie.Linux.Native;

/// <summary>
/// P/Invoke into WebKitGTK 4.1 and its JavaScriptCore-GTK companion — the web view, its user-content manager
/// (script-message bridge + document-start user scripts), the custom <c>app://</c> URI scheme, and outbound
/// <c>evaluateJavaScript</c>. 4.1 is the libsoup3 ABI shipped by current distros (<c>libwebkit2gtk-4.1-0</c>).
/// </summary>
internal static partial class WebKit {
	private const string Lib = "libwebkit2gtk-4.1.so.0";
	private const string Jsc = "libjavascriptcoregtk-4.1.so.0";

	/// <summary><c>WEBKIT_USER_CONTENT_INJECT_TOP_FRAME</c> — inject user scripts into the top frame only.</summary>
	internal const int InjectTopFrame = 1;

	/// <summary><c>WEBKIT_USER_SCRIPT_INJECT_AT_DOCUMENT_START</c> — run user scripts before the page loads.</summary>
	internal const int InjectAtDocumentStart = 0;

	[LibraryImport(Lib)]
	internal static partial IntPtr webkit_web_context_get_default();

	[LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial void webkit_web_context_register_uri_scheme(
		IntPtr context, string scheme, IntPtr callback, IntPtr userData, IntPtr destroyNotify);

	[LibraryImport(Lib)]
	internal static partial IntPtr webkit_user_content_manager_new();

	[LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool webkit_user_content_manager_register_script_message_handler(IntPtr manager, string name);

	[LibraryImport(Lib)]
	internal static partial void webkit_user_content_manager_add_script(IntPtr manager, IntPtr script);

	[LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial IntPtr webkit_user_script_new(
		string source, int injectedFrames, int injectionTime, IntPtr allowList, IntPtr blockList);

	[LibraryImport(Lib)]
	internal static partial IntPtr webkit_web_view_new_with_user_content_manager(IntPtr manager);

	[LibraryImport(Lib)]
	internal static partial IntPtr webkit_web_view_get_settings(IntPtr webView);

	[LibraryImport(Lib)]
	internal static partial void webkit_settings_set_enable_developer_extras(
		IntPtr settings, [MarshalAs(UnmanagedType.Bool)] bool enabled);

	[LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial void webkit_web_view_load_uri(IntPtr webView, string uri);

	[LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial void webkit_web_view_evaluate_javascript(
		IntPtr webView, string script, nint length, IntPtr worldName, IntPtr sourceUri,
		IntPtr cancellable, IntPtr callback, IntPtr userData);

	[LibraryImport(Lib)]
	internal static partial IntPtr webkit_uri_scheme_request_get_path(IntPtr request);

	[LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial void webkit_uri_scheme_request_finish(
		IntPtr request, IntPtr stream, long streamLength, string contentType);

	[LibraryImport(Lib)]
	internal static partial IntPtr webkit_javascript_result_get_js_value(IntPtr jsResult);

	[LibraryImport(Jsc)]
	internal static partial IntPtr jsc_value_to_string(IntPtr value);
}
