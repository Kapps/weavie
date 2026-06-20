using System.Runtime.InteropServices;

namespace Weavie.Win.Terminal;

/// <summary>
/// kernel32 P/Invoke for a Windows pseudo console (ConPTY). We create a pair of anonymous pipes,
/// hand the read-end of one and the write-end of the other to <c>CreatePseudoConsole</c>, then
/// launch the child with a <c>PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE</c> attribute pointing at the
/// resulting <c>HPCON</c>. ConHost drives the child's console; we keep the opposite pipe ends to
/// feed keystrokes in and stream rendered output out. This is the Windows analogue of the macOS
/// <c>posix_openpt</c>/<c>posix_spawn</c> path in Weavie.Core. ConPTY needs Windows 10 1809+.
/// </summary>
internal static partial class ConPtyNativeMethods {
	// ProcThreadAttribute for attaching a pseudo console to a child process.
	internal const nint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

	// CreateProcess creation flags.
	internal const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
	internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

	// StartupInfo.dwFlags: honor the explicit hStdInput/hStdOutput/hStdError (here set to NULL so the
	// ConPTY child does not inherit the parent's redirected std handles — see SpawnChild).
	internal const int STARTF_USESTDHANDLES = 0x00000100;

	[StructLayout(LayoutKind.Sequential)]
	internal struct Coord {
		public short X;
		public short Y;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct StartupInfoEx {
		public StartupInfo StartupInfo;
		public nint lpAttributeList;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct StartupInfo {
		public int cb;
		public nint lpReserved;
		public nint lpDesktop;
		public nint lpTitle;
		public int dwX;
		public int dwY;
		public int dwXSize;
		public int dwYSize;
		public int dwXCountChars;
		public int dwYCountChars;
		public int dwFillAttribute;
		public int dwFlags;
		public short wShowWindow;
		public short cbReserved2;
		public nint lpReserved2;
		public nint hStdInput;
		public nint hStdOutput;
		public nint hStdError;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct ProcessInformation {
		public nint hProcess;
		public nint hThread;
		public int dwProcessId;
		public int dwThreadId;
	}

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool CreatePipe(out nint hReadPipe, out nint hWritePipe, nint lpPipeAttributes, uint nSize);

	[LibraryImport("kernel32.dll")]
	internal static partial int CreatePseudoConsole(Coord size, nint hInput, nint hOutput, uint dwFlags, out nint phPC);

	[LibraryImport("kernel32.dll")]
	internal static partial int ResizePseudoConsole(nint hPC, Coord size);

	[LibraryImport("kernel32.dll")]
	internal static partial void ClosePseudoConsole(nint hPC);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool InitializeProcThreadAttributeList(
		nint lpAttributeList, int dwAttributeCount, int dwFlags, ref nint lpSize);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool UpdateProcThreadAttribute(
		nint lpAttributeList, uint dwFlags, nint attribute, nint lpValue, nint cbSize, nint lpPreviousValue, nint lpReturnSize);

	[LibraryImport("kernel32.dll")]
	internal static partial void DeleteProcThreadAttributeList(nint lpAttributeList);

	[LibraryImport("kernel32.dll", SetLastError = true, EntryPoint = "CreateProcessW", StringMarshalling = StringMarshalling.Utf16)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool CreateProcess(
		string? lpApplicationName,
		string? lpCommandLine,
		nint lpProcessAttributes,
		nint lpThreadAttributes,
		[MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
		uint dwCreationFlags,
		nint lpEnvironment,
		string? lpCurrentDirectory,
		ref StartupInfoEx lpStartupInfo,
		out ProcessInformation lpProcessInformation);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static unsafe partial bool ReadFile(
		nint hFile, byte* lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, nint lpOverlapped);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static unsafe partial bool WriteFile(
		nint hFile, byte* lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, nint lpOverlapped);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool CloseHandle(nint hObject);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	internal static partial uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool GetExitCodeProcess(nint hProcess, out uint lpExitCode);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool TerminateProcess(nint hProcess, uint uExitCode);
}
