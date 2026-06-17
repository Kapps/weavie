using System.Runtime.InteropServices;

namespace Weavie.Win.Hosting;

/// <summary>
/// Win32 chrome tweaks the WinForms API doesn't expose — currently the immersive dark title bar (via DWM),
/// so the window caption matches the dark app instead of the default light frame.
/// </summary>
internal static class NativeChrome {
	// DWMWA_USE_IMMERSIVE_DARK_MODE on Windows 10 20H1+ (older builds used 19); a wrong value is a harmless no-op.
	private const int DwmwaUseImmersiveDarkMode = 20;

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

	/// <summary>Switches <paramref name="handle"/>'s title bar to dark mode. No-op when the handle is null or DWM rejects it.</summary>
	public static void UseDarkTitleBar(IntPtr handle) {
		if (handle == IntPtr.Zero) {
			return;
		}

		int enabled = 1;
		_ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
	}
}
