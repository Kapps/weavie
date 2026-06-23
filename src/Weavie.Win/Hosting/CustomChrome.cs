using System.Runtime.InteropServices;
using Weavie.Core.Shell;

namespace Weavie.Win.Hosting;

/// <summary>
/// Win32 helpers for a frameless host window: keeps the real <c>WS_THICKFRAME | WS_CAPTION</c> styles (Aero Snap,
/// shadow, work-area maximize) but strips the visual caption via <c>WM_NCCALCSIZE</c>, with drag/resize driven from
/// the web side since the <c>Dock.Fill</c> WebView2 swallows the mouse at the edges. A <see cref="Form"/> delegates
/// its <c>WndProc</c> here.
/// </summary>
internal static class CustomChrome {
	private const int WmNcCalcSize = 0x0083;
	private const int WmNcHitTest = 0x0084;
	private const int WmNcLButtonDown = 0x00A1;
	private const int WmSysCommand = 0x0112;

	// Keyboard menu-bar activation (Alt / F10); low 4 bits are reserved, so compare with the 0xFFF0 mask.
	private const int ScKeyMenu = 0xF100;

	private const int HtClient = 1;
	private const int HtLeft = 10;
	private const int HtRight = 11;
	private const int HtTop = 12;
	private const int HtTopLeft = 13;
	private const int HtTopRight = 14;
	private const int HtBottom = 15;
	private const int HtBottomLeft = 16;
	private const int HtBottomRight = 17;

	private const int SmCxFrame = 32;
	private const int SmCyFrame = 33;
	private const int SmCxPaddedBorder = 92;

	/// <summary>Grab width (px) of the window-edge resize zones.</summary>
	private const int ResizeBorder = 8;

	[StructLayout(LayoutKind.Sequential)]
	private struct Rect {
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct NcCalcSizeParams {
		public Rect Proposed;       // rgrc[0]: proposed window rect (in/out: the new client rect)
		public Rect ValidDest;      // rgrc[1]
		public Rect ValidSrc;       // rgrc[2]
		public IntPtr WindowPos;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Margins {
		public int Left;
		public int Right;
		public int Top;
		public int Bottom;
	}

	// DPI-aware (Win10 1607+): frame thickness for the window's *current* monitor, so the maximized inset is
	// right on a scaled monitor — unlike GetSystemMetrics's primary/system DPI.
	[DllImport("user32.dll")]
	private static extern int GetSystemMetricsForDpi(int index, uint dpi);

	[DllImport("user32.dll")]
	private static extern uint GetDpiForWindow(IntPtr hwnd);

	// Live maximized state (WS_MAXIMIZE), correct inside WM_NCCALCSIZE — unlike Form.WindowState.
	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsZoomed(IntPtr hwnd);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool ReleaseCapture();

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

	[DllImport("dwmapi.dll")]
	private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

	/// <summary>
	/// True when <paramref name="handle"/> is maximized, read live (WS_MAXIMIZE). Use inside <c>WndProc</c> rather
	/// than <see cref="Form.WindowState"/>, which WinForms updates only on <c>WM_SIZE</c> (after <c>WM_NCCALCSIZE</c>).
	/// </summary>
	public static bool IsMaximized(IntPtr handle) => handle != IntPtr.Zero && IsZoomed(handle);

	/// <summary>
	/// Extends a 1px DWM frame into the client area so the OS keeps drawing the drop shadow and rounded corners
	/// despite the removed caption. No-op for a null handle.
	/// </summary>
	public static void EnableShadow(IntPtr handle) {
		if (handle == IntPtr.Zero) {
			return;
		}

		var margins = new Margins { Left = 0, Right = 0, Top = 1, Bottom = 0 };
		_ = DwmExtendFrameIntoClientArea(handle, ref margins);
	}

	/// <summary>
	/// Handles <c>WM_NCCALCSIZE</c>: makes the whole window rect the client area (removing the caption), insetting
	/// by the frame when maximized so the overhang isn't clipped and content doesn't spill over the taskbar.
	/// Returns true when it consumed the message.
	/// </summary>
	public static bool HandleNcCalcSize(ref Message message, bool maximized) {
		if (message.Msg != WmNcCalcSize || message.WParam == IntPtr.Zero) {
			return false;
		}

		if (maximized) {
			var p = Marshal.PtrToStructure<NcCalcSizeParams>(message.LParam);
			uint dpi = GetDpiForWindow(message.HWnd);
			if (dpi == 0) {
				dpi = 96;
			}

			int frameX = GetSystemMetricsForDpi(SmCxFrame, dpi) + GetSystemMetricsForDpi(SmCxPaddedBorder, dpi);
			int frameY = GetSystemMetricsForDpi(SmCyFrame, dpi) + GetSystemMetricsForDpi(SmCxPaddedBorder, dpi);
			p.Proposed.Left += frameX;
			p.Proposed.Top += frameY;
			p.Proposed.Right -= frameX;
			p.Proposed.Bottom -= frameY;
			Marshal.StructureToPtr(p, message.LParam, fDeleteOld: false);
		}

		// Leaving rgrc[0] unchanged makes client == window: caption/borders vanish while WS_THICKFRAME stays for
		// snap/shadow. Resize edges come back via WM_NCHITTEST.
		message.Result = IntPtr.Zero;
		return true;
	}

	/// <summary>
	/// Handles <c>WM_NCHITTEST</c>: returns the resize-edge code for the outer <see cref="ResizeBorder"/>px margin,
	/// else <c>HTCLIENT</c> (the WebView interior supplies <c>HTCAPTION</c> via <c>app-region: drag</c>). No resize
	/// while maximized. Returns true when it consumed the message.
	/// </summary>
	public static bool HandleNcHitTest(IntPtr handle, ref Message message, bool maximized) {
		if (message.Msg != WmNcHitTest) {
			return false;
		}

		if (maximized) {
			message.Result = (IntPtr)HtClient;
			return true;
		}

		long lparam = message.LParam.ToInt64();
		int x = unchecked((short)(lparam & 0xFFFF));
		int y = unchecked((short)((lparam >> 16) & 0xFFFF));
		if (!GetWindowRect(handle, out var r)) {
			message.Result = (IntPtr)HtClient;
			return true;
		}

		bool left = x < r.Left + ResizeBorder;
		bool right = x >= r.Right - ResizeBorder;
		bool top = y < r.Top + ResizeBorder;
		bool bottom = y >= r.Bottom - ResizeBorder;

		int hit = (top, bottom, left, right) switch {
			(true, _, true, _) => HtTopLeft,
			(true, _, _, true) => HtTopRight,
			(_, true, true, _) => HtBottomLeft,
			(_, true, _, true) => HtBottomRight,
			(true, _, _, _) => HtTop,
			(_, true, _, _) => HtBottom,
			(_, _, true, _) => HtLeft,
			(_, _, _, true) => HtRight,
			_ => HtClient,
		};
		message.Result = (IntPtr)hit;
		return true;
	}

	/// <summary>
	/// Swallows bare <c>Alt</c>/<c>F10</c> menu-bar activation (<c>SC_KEYMENU</c>, <c>lParam == 0</c>): this frameless
	/// window has no native menu bar, so entering menu mode would freeze input until the next keypress beeps.
	/// <c>Alt+Space</c> carries the space mnemonic in <c>lParam</c>, so it falls through to the system menu.
	/// Returns true when it consumed the message.
	/// </summary>
	public static bool HandleSysKeyMenu(ref Message message) {
		if (message.Msg != WmSysCommand
			|| (message.WParam.ToInt64() & 0xFFF0) != ScKeyMenu
			|| message.LParam != IntPtr.Zero) {
			return false;
		}

		message.Result = IntPtr.Zero;
		return true;
	}

	/// <summary>
	/// Begins a native OS resize of <paramref name="handle"/> from <paramref name="edge"/>, following the cursor
	/// until release. The frameless window's resize path: the WebView draws grab handles and asks the host to call
	/// this on mousedown. No-op for a null handle or an unmapped edge.
	/// </summary>
	public static void StartResize(IntPtr handle, ResizeEdge edge) {
		if (handle == IntPtr.Zero) {
			return;
		}

		int hit = edge switch {
			ResizeEdge.Left => HtLeft,
			ResizeEdge.Right => HtRight,
			ResizeEdge.Top => HtTop,
			ResizeEdge.Bottom => HtBottom,
			ResizeEdge.TopLeft => HtTopLeft,
			ResizeEdge.TopRight => HtTopRight,
			ResizeEdge.BottomLeft => HtBottomLeft,
			ResizeEdge.BottomRight => HtBottomRight,
			_ => HtClient,
		};
		if (hit == HtClient) {
			return;
		}

		// ReleaseCapture + WM_NCLBUTTONDOWN enters the OS modal resize loop, which tracks the cursor itself — so
		// lParam (the click point) is unused and passed as zero.
		ReleaseCapture();
		_ = SendMessage(handle, WmNcLButtonDown, (IntPtr)hit, IntPtr.Zero);
	}
}
