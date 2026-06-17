using System.Runtime.InteropServices;

namespace Weavie.Win.Hosting;

/// <summary>
/// Win32 helpers that turn a normal WinForms window into a "frameless but functional" one — the host
/// frame for a VS Code–style title bar drawn entirely in the web content. The window keeps its real
/// <c>WS_THICKFRAME | WS_CAPTION</c> styles (so Aero Snap, the drop shadow, and work-area maximize all
/// keep working), but the visual caption is removed by intercepting <c>WM_NCCALCSIZE</c> and the resize
/// borders are re-supplied via <c>WM_NCHITTEST</c>. Window <em>dragging</em> comes from the web side:
/// WebView2's non-client-region support maps CSS <c>app-region: drag</c> to <c>HTCAPTION</c>, so this
/// only owns the frame geometry + resize edges. A <see cref="Form"/> delegates its <c>WndProc</c> here.
/// </summary>
internal static class CustomChrome {
	private const int WmNcCalcSize = 0x0083;
	private const int WmNcHitTest = 0x0084;

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

	[DllImport("user32.dll")]
	private static extern int GetSystemMetrics(int index);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

	[DllImport("dwmapi.dll")]
	private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

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
			int frameX = GetSystemMetrics(SmCxFrame) + GetSystemMetrics(SmCxPaddedBorder);
			int frameY = GetSystemMetrics(SmCyFrame) + GetSystemMetrics(SmCxPaddedBorder);
			p.Proposed.Left += frameX;
			p.Proposed.Top += frameY;
			p.Proposed.Right -= frameX;
			p.Proposed.Bottom -= frameY;
			Marshal.StructureToPtr(p, message.LParam, fDeleteOld: false);
		}

		// Leaving rgrc[0] otherwise unchanged makes client == window: the caption/borders vanish visually
		// while WS_THICKFRAME stays for snap/shadow. Resize edges come back via WM_NCHITTEST below.
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
}
