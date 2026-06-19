using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Weavie.Linux.Native;

/// <summary>
/// Marshals work onto the GTK main thread. GTK/WebKit calls are not thread-safe, but the host is
/// driven from background threads (PTY output, the change tracker, MCP/command callbacks), so every
/// touch of the window or web view goes through <see cref="Invoke"/>, which queues the action on the
/// GLib idle source and runs it on the next main-loop iteration (FIFO, so message order is preserved).
/// </summary>
internal static class GtkMain {
	// A single native trampoline (kept alive for the process) recovers the queued action from its
	// GCHandle, runs it, and frees the handle. Actions are parked in a table keyed by a token rather
	// than passing a managed pointer through native code.
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
