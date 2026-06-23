using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Weavie.Linux.Native;

/// <summary>
/// Marshals work onto the GTK main thread via the GLib idle source (FIFO, preserving message order). GTK/WebKit
/// calls are not thread-safe, so every window or web-view touch from a background thread goes through
/// <see cref="Invoke"/>.
/// </summary>
internal static class GtkMain {
	// One kept-alive trampoline; actions are parked in a token-keyed table so no managed pointer crosses native.
	private static readonly GSourceFunc Trampoline = RunQueued;
	private static readonly IntPtr TrampolinePtr = Marshal.GetFunctionPointerForDelegate(Trampoline);
	private static readonly ConcurrentDictionary<nint, Action> Pending = new();
	private static long _nextToken;

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
