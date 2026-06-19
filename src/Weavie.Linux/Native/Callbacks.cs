using System.Runtime.InteropServices;

namespace Weavie.Linux.Native;

// Native callback signatures used with g_signal_connect_data / register-scheme / g_idle_add.
// They are marshalled as plain function pointers (IntPtr), so every instance handed to native code
// MUST be kept alive (a field) for as long as native may call it, or the GC will collect it and the
// call will crash. All use the C calling convention (cdecl) that GLib/GTK/WebKit export.

/// <summary>GLib <c>GSourceFunc</c>: a one-shot/idle callback returning a gboolean (0 = remove the source).</summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int GSourceFunc(IntPtr userData);

/// <summary>GLib <c>GDestroyNotify</c>: frees user data owned by a native object when it is destroyed.</summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void GDestroyNotify(IntPtr data);

/// <summary>GTK <c>GCallback</c> for a widget signal with no extra args (e.g. <c>destroy</c>).</summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WidgetCallback(IntPtr widget, IntPtr userData);

/// <summary>WebKit <c>script-message-received</c> handler: <c>(manager, WebKitJavascriptResult*, userData)</c>.</summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void ScriptMessageCallback(IntPtr manager, IntPtr jsResult, IntPtr userData);

/// <summary>WebKit <c>WebKitURISchemeRequestCallback</c>: <c>(WebKitURISchemeRequest*, userData)</c>.</summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void UriSchemeRequestCallback(IntPtr request, IntPtr userData);
