using System.Runtime.InteropServices;

namespace Weavie.Linux.Native;

/// <summary>
/// Turns one of GTK 4's async calls back into the synchronous answer the host bus and the shell menu expect,
/// by running a nested main loop until the operation completes — the same nesting GTK 3's <c>_run</c> and
/// <c>wait_for</c> entry points did on the caller's behalf.
/// </summary>
internal static class MainLoopWait {
	/// <summary>
	/// Starts an async GIO operation with <paramref name="begin"/> and pumps the main loop until it finishes,
	/// returning what <paramref name="complete"/> makes of the result (which is only valid inside the callback).
	/// </summary>
	internal static T For<T>(Action<IntPtr> begin, Func<IntPtr, T> complete) {
		ArgumentNullException.ThrowIfNull(begin);
		ArgumentNullException.ThrowIfNull(complete);
		IntPtr loop = GLib.g_main_loop_new(IntPtr.Zero, false);
		T value = default!;
		AsyncReadyCallback finished = (_, result, _) => {
			value = complete(result);
			GLib.g_main_loop_quit(loop);
		};

		try {
			begin(Marshal.GetFunctionPointerForDelegate(finished));
			GLib.g_main_loop_run(loop);
			return value;
		} finally {
			GC.KeepAlive(finished);
			GLib.g_main_loop_unref(loop);
		}
	}
}
