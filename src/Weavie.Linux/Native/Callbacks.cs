using System.Runtime.InteropServices;

namespace Weavie.Linux.Native;

// Native callback signatures (all cdecl, matching GLib/GTK/WebKit). Marshalled as plain function pointers, so
// every instance handed to native code MUST be kept alive in a field while native may call it, or the GC
// collects it and the call crashes.

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
