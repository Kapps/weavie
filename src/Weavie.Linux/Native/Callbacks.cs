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

/// <summary>GObject <c>notify</c> handler: <c>(instance, GParamSpec*, userData)</c>.</summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PropertyNotifyCallback(IntPtr instance, IntPtr property, IntPtr userData);

/// <summary>GTK <c>key-pressed</c> handler on a key controller: <c>(controller, keyval, keycode, state, userData)</c>.</summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int KeyPressedCallback(IntPtr controller, uint keyval, uint keycode, uint state, IntPtr userData);

/// <summary>GIO <c>GAsyncReadyCallback</c>: <c>(sourceObject, GAsyncResult*, userData)</c>.</summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void AsyncReadyCallback(IntPtr sourceObject, IntPtr result, IntPtr userData);

/// <summary>GLib <c>GUnixFDSourceFunc</c>: <c>(fd, condition, userData)</c>, returning 0 to remove the source.</summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int UnixFdSourceFunc(int fd, int condition, IntPtr userData);

/// <summary>Xlib error handler: <c>(display, XErrorEvent*)</c>; the return value is ignored.</summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int X11ErrorHandler(IntPtr display, IntPtr error);

/// <summary>GIO <c>GListModel::items-changed</c> handler: <c>(model, position, removed, added, userData)</c>.</summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void ItemsChangedCallback(IntPtr model, uint position, uint removed, uint added, IntPtr userData);

/// <summary>WebKit <c>script-message-received</c> handler: <c>(manager, JSCValue*, userData)</c>.</summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void ScriptMessageCallback(IntPtr manager, IntPtr jsValue, IntPtr userData);

/// <summary>WebKit <c>WebKitURISchemeRequestCallback</c>: <c>(WebKitURISchemeRequest*, userData)</c>.</summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void UriSchemeRequestCallback(IntPtr request, IntPtr userData);
