using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Weavie.Linux.Native;

/// <summary>
/// Marshals work onto the GTK main thread via the GLib idle source (FIFO, preserving message order). GTK/WebKit
/// calls are not thread-safe, so every window or web-view touch from a background thread goes through
/// <see cref="Invoke"/>.
/// </summary>
internal static class GtkMain {
	private static IntPtr _loop;

	// One kept-alive trampoline; actions are parked in a token-keyed table so no managed pointer crosses native.
	private static readonly GSourceFunc Trampoline = RunQueued;
	private static readonly IntPtr TrampolinePtr = Marshal.GetFunctionPointerForDelegate(Trampoline);
	private static readonly ConcurrentDictionary<nint, Action> Pending = new();
	private static long _nextToken;

	/// <summary>Runs the GTK main loop until <see cref="Quit"/>. GTK 4 has no <c>gtk_main</c>; the loop is ours.</summary>
	internal static void Run() {
		_loop = GLib.g_main_loop_new(IntPtr.Zero, false);
		GLib.g_main_loop_run(_loop);
		GLib.g_main_loop_unref(_loop);
		_loop = IntPtr.Zero;
	}

	/// <summary>Ends the loop <see cref="Run"/> is pumping, returning control to the host's shutdown.</summary>
	internal static void Quit() => GLib.g_main_loop_quit(_loop);

	/// <summary>Queues <paramref name="action"/> to run on the GTK main thread.</summary>
	internal static void Invoke(Action action) {
		ArgumentNullException.ThrowIfNull(action);
		nint token = (nint)Interlocked.Increment(ref _nextToken);
		Pending[token] = action;
		GLib.g_idle_add(TrampolinePtr, token);
	}

	private static int RunQueued(IntPtr token) {
		if (Pending.TryRemove(token, out var action)) {
			try {
				action();
			} catch (Exception ex) {
				Console.Error.WriteLine($"[weavie] main-thread action threw: {ex}");
			}
		}

		return 0; // G_SOURCE_REMOVE — one-shot.
	}
}
