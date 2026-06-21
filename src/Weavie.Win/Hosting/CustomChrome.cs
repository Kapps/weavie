using System.Runtime.InteropServices;
using Weavie.Core.Shell;

namespace Weavie.Win.Hosting;

/// <summary>
/// Win32 helpers that turn a normal WinForms window into a "frameless but functional" one — the host frame
/// for a VS Code–style title bar drawn entirely in the web content. The window keeps its real
/// <c>WS_THICKFRAME | WS_CAPTION</c> styles (so Aero Snap, the drop shadow, and work-area maximize keep
/// working), but the visual caption is removed by intercepting <c>WM_NCCALCSIZE</c>. Both window
/// <em>dragging</em> and <em>resizing</em> are driven from the web side, since the <c>Dock.Fill</c> WebView2
/// covers the whole client area and swallows the mouse at the window edges:
/// <list type="bullet">
/// <item>Drag: WebView2's non-client-region support maps CSS <c>app-region: drag</c> to <c>HTCAPTION</c>.</item>
/// <item>Resize: the web draws grab handles at the border and calls <see cref="StartResize"/> on mousedown,
///   which hands off to the OS's native resize loop (WebView2 has no resize equivalent of <c>app-region</c>).</item>
/// </list>
/// A <see cref="Form"/> delegates its <c>WndProc</c> here.
/// </summary>
internal static class CustomChrome {
	private const int WmNcCalcSize = 0x0083;
	private const int WmNcHitTest = 0x0084;
	private const int WmNcLButtonDown = 0x00A1;
	private const int WmSysCommand = 0x0112;

	// System command for keyboard menu-bar activation (Alt / F10). The low 4 bits are reserved, so compare
	// against the upper bits with the 0xFFF0 mask.
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
		public Rect Proposed;       // rgrc[0]: the proposed window rect (in/out: the new client rect)
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

	// DPI-aware metrics (Win10 1607+): the frame thickness for the window's *current* monitor, not the
	// primary/system DPI that GetSystemMetrics reports — so the maximized inset is right on a scaled monitor.
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

	// Hand off to the OS's native resize loop: release any in-process mouse capture (the WebView grabbed it
	// on the handle's mousedown), then post the non-client button-down the OS uses to begin edge tracking.
	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool ReleaseCapture();

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

	[DllImport("dwmapi.dll")]
	private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

	/// <summary>
	/// True when <paramref name="handle"/> is maximized, read live (WS_MAXIMIZE). Use this inside <c>WndProc</c>
	/// rather than <see cref="Form.WindowState"/>: WinForms updates WindowState on <c>WM_SIZE</c>, which fires
	/// <em>after</em> <c>WM_NCCALCSIZE</c>, so during a maximize the NCCALCSIZE handler would otherwise see the
	/// old (restored) state and skip the frame inset.
	/// </summary>
	public static bool IsMaximized(IntPtr handle) => handle != IntPtr.Zero && IsZoomed(handle);

	/// <summary>
	/// Extends a 1px DWM frame into the client area so the OS keeps drawing the window's drop shadow and
	/// rounded corners even though the caption is gone. No-op for a null handle.
	/// </summary>
	public static void EnableShadow(IntPtr handle) {
		if (handle == IntPtr.Zero) {
			return;
		}

		var margins = new Margins { Left = 0, Right = 0, Top = 1, Bottom = 0 };
		_ = DwmExtendFrameIntoClientArea(handle, ref margins);
	}

	/// <summary>
	/// Handles <c>WM_NCCALCSIZE</c>: makes the whole window rect the client area (removing the caption).
	/// When maximized, insets the client by the frame so the off-screen maximize overhang isn't clipped
	/// and the content doesn't spill over the taskbar. Returns true when it consumed the message.
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

		// Leaving rgrc[0] otherwise unchanged makes client == window: the caption/borders vanish visually
		// while WS_THICKFRAME stays for snap/shadow. Resize edges come back via WM_NCHITTEST.
		message.Result = IntPtr.Zero;
		return true;
	}

	/// <summary>
	/// Handles <c>WM_NCHITTEST</c>: returns the resize-edge code for the outer <see cref="ResizeBorder"/>px
	/// margin (so edges/corners resize), else <c>HTCLIENT</c> — the interior is the WebView, whose
	/// <c>app-region: drag</c> regions supply <c>HTCAPTION</c> for window dragging. No resize while maximized.
	/// Returns true when it consumed the message.
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
	/// Swallows the keyboard menu-bar activation (<c>WM_SYSCOMMAND</c> / <c>SC_KEYMENU</c> with no mnemonic)
	/// that bare <c>Alt</c> and <c>F10</c> generate. This frameless window has no native menu bar, so entering
	/// menu mode would focus a menu that isn't there: input freezes until another keypress, which then beeps.
	/// <para>
	/// Only the bare activation (<c>lParam == 0</c>) is consumed. <c>Alt+Space</c> arrives as <c>SC_KEYMENU</c>
	/// with the space mnemonic in <c>lParam</c>, so it falls through and still opens the window's system menu
	/// (Move/Size/Maximize/Close). Returns true when it consumed the message.
	/// </para>
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
	/// until the button is released. The working resize path for the frameless window: the WebView2 fills the
	/// client area and swallows the mouse at the edges, so the web draws grab handles and, on mousedown, asks
	/// the host to call this. No-op for a null handle or an unmapped edge.
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

		// ReleaseCapture + WM_NCLBUTTONDOWN(<edge HT>) enters the OS modal resize loop, which tracks the live
		// cursor itself — so lParam (the click point) is unused and passed as zero.
		ReleaseCapture();
		_ = SendMessage(handle, WmNcLButtonDown, (IntPtr)hit, IntPtr.Zero);
	}
}
