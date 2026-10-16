using System.ComponentModel;
using System.Runtime.InteropServices;

// Puts a Windows console's input in raw VT mode the way a real TUI does, so a bracketed paste reaches the process as the
// bytes the host wrote instead of being parsed into keystrokes by the line editor.
internal static partial class ConsoleInput {
	private const int StdInputHandle = -10;
	private const uint ProcessedInput = 0x1;
	private const uint LineInput = 0x2;
	private const uint EchoInput = 0x4;
	private const uint VirtualTerminalInput = 0x200;

	public static void EnableRawVirtualTerminal() {
		if (!OperatingSystem.IsWindows()) return;
		nint handle = GetStdHandle(StdInputHandle);
		// A redirected stdin has no console mode to set.
		if (!GetConsoleMode(handle, out uint mode)) return;
		mode = (mode & ~(ProcessedInput | LineInput | EchoInput)) | VirtualTerminalInput;
		if (!SetConsoleMode(handle, mode)) throw new Win32Exception(Marshal.GetLastWin32Error());
	}

	[LibraryImport("kernel32.dll")]
	private static partial nint GetStdHandle(int handle);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GetConsoleMode(nint handle, out uint mode);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetConsoleMode(nint handle, uint mode);
}
