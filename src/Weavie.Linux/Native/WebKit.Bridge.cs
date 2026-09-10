using System.Runtime.InteropServices;

namespace Weavie.Linux.Native;

internal static partial class WebKit {
	[LibraryImport(Lib)]
	internal static partial void webkit_user_script_unref(IntPtr script);

	[LibraryImport(Lib)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool webkit_response_policy_decision_is_main_frame_main_resource(IntPtr decision);

	[LibraryImport(Lib)]
	internal static partial IntPtr webkit_response_policy_decision_get_response(IntPtr decision);

	[LibraryImport(Lib)]
	internal static partial IntPtr webkit_uri_response_get_uri(IntPtr response);

	[LibraryImport(Lib)]
	internal static partial void webkit_policy_decision_ignore(IntPtr decision);

	[LibraryImport(Jsc)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool jsc_value_is_string(IntPtr value);
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int PolicyDecisionCallback(IntPtr webView, IntPtr decision, int decisionType, IntPtr userData);
