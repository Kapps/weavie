using System.Runtime.InteropServices;

namespace Weavie.Win.Hosting;

/// <summary>
/// Foreground/visibility helpers behind <c>weavie.window.toggle</c> (the global <c>ctrl+`</c> hotkey).
/// WinForms' <c>Activate()</c> alone doesn't reliably steal foreground across processes; pairing it with
/// <c>SetForegroundWindow</c> does, since the triggering <c>WM_HOTKEY</c>/command grants our process the right
/// to set the foreground window. All methods must be called on the UI thread.
/// </summary>
internal static class WindowFocus {
	private const uint GwHwndNext = 2;     // GW_HWNDNEXT — next window down the Z-order
	private const uint GwOwner = 4;        // GW_OWNER
	private const int GwlExStyle = -20;    // GWL_EXSTYLE
	private const long WsExToolWindow = 0x00000080;
	private static readonly IntPtr HwndBottom = new(1);
	private const uint SwpNoSize = 0x0001;
	private const uint SwpNoMove = 0x0002;
	private const uint SwpNoActivate = 0x0010;

	/// <summary>Whether <paramref name="window"/> is currently the OS foreground (focused) window.</summary>
	public static bool IsForeground(Form window) {
		ArgumentNullException.ThrowIfNull(window);
		return window.IsHandleCreated && GetForegroundWindow() == window.Handle;
	}

	/// <summary>Restores (if minimized), shows, activates, and forces <paramref name="window"/> to the foreground.</summary>
	public static void ForceForeground(Form window) {
		ArgumentNullException.ThrowIfNull(window);
		if (window.WindowState == FormWindowState.Minimized) {
			window.WindowState = FormWindowState.Normal;
		}

		if (!window.Visible) {
			window.Show();
		}

		window.Activate();
		window.BringToFront();
		SetForegroundWindow(window.Handle);
	}

	/// <summary>
	/// Toggles <paramref name="window"/>: bring it to the foreground when behind, or — when already foreground —
	/// drop it behind by handing focus back to the previously focused window (no minimize). Behind the global
	/// focus hotkey.
	/// </summary>
	public static void Toggle(Form window) {
		ArgumentNullException.ThrowIfNull(window);
		if (IsForeground(window)) {
			DropBehind(window);
		} else {
			ForceForeground(window);
		}
	}

	// Relinquish foreground without minimizing: activate the next activatable top-level window beneath ours in
	// the Z-order (the one focused before we raised ours), so it regains focus and our window drops behind it,
	// still visible. Allowed because Toggle only calls this when our window owns the foreground. If nothing
	// activatable sits behind us (essentially only the bare desktop), sink to the bottom of the Z-order.
	private static void DropBehind(Form window) {
		IntPtr next = GetWindow(window.Handle, GwHwndNext);
		while (next != IntPtr.Zero) {
			if (IsActivatable(next)) {
				SetForegroundWindow(next);
				return;
			}

			next = GetWindow(next, GwHwndNext);
		}

		SetWindowPos(window.Handle, HwndBottom, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
	}

	// A reasonable "could be the previously-focused app window" filter: visible, not minimized, top-level
	// (no owner), and not a tool window (tooltips, palettes). Mirrors the common Alt+Tab eligibility heuristic.
	private static bool IsActivatable(IntPtr hwnd) {
		if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || GetWindow(hwnd, GwOwner) != IntPtr.Zero) {
			return false;
		}

		return (GetWindowLong(hwnd, GwlExStyle) & WsExToolWindow) == 0;
	}

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetForegroundWindow(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern IntPtr GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsWindowVisible(IntPtr hWnd);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsIconic(IntPtr hWnd);

	[DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
	private static extern long GetWindowLong(IntPtr hWnd, int nIndex);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
