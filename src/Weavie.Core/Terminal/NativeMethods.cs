using System.Runtime.InteropServices;

namespace Weavie.Core.Terminal;

/// <summary>
/// libc P/Invoke for a POSIX PTY (macOS + Linux). We open a pseudo-terminal master and launch the
/// child via <c>posix_spawn</c> with <c>POSIX_SPAWN_SETSID</c> — the child becomes a session leader
/// and, because the spawn file-actions open the slave tty (no O_NOCTTY) after setsid, acquires it
/// as its controlling terminal. This avoids calling <c>fork()</c> from the managed runtime
/// (unsafe with the .NET thread pool); only async-signal-safe work happens in the child. The few
/// constants that differ between the two kernels are selected at runtime.
/// </summary>
internal static partial class NativeMethods {
	// open(2) flags. O_RDWR is the same on both; O_NOCTTY differs (macOS 0x20000, Linux 0400).
	internal const int O_RDWR = 0x0002;
	internal static readonly int O_NOCTTY = OperatingSystem.IsMacOS() ? 0x20000 : 0x100;

	// posix_spawn attribute flags. POSIX_SPAWN_SETSID differs (macOS 0x0400, glibc 0x80).
	// POSIX_SPAWN_CLOEXEC_DEFAULT is Apple-only — on Linux the master fd is made close-on-exec via
	// fcntl(F_SETFD) instead (see PosixPtyTerminal.Start) so it never leaks into the spawned child.
	internal static readonly short POSIX_SPAWN_SETSID = (short)(OperatingSystem.IsMacOS() ? 0x0400 : 0x80);
	internal const short POSIX_SPAWN_CLOEXEC_DEFAULT = 0x4000;

	// fcntl(2) for the Linux close-on-exec path described above.
	internal const int F_SETFD = 2;
	internal const int FD_CLOEXEC = 1;

	// ioctl request: TIOCSWINSZ = _IOW('t', 103, struct winsize) on macOS; fixed 0x5414 on Linux.
	internal static readonly nuint TIOCSWINSZ = OperatingSystem.IsMacOS() ? 0x80087467 : 0x5414;

	// Caller-allocated storage size for posix_spawn_file_actions_t / posix_spawnattr_t. Generously larger
	// than either platform's struct (glibc's posix_spawnattr_t is ~336 bytes; macOS uses an 8-byte opaque
	// pointer) so *_init can initialize the object in place without overrunning the buffer.
	internal const int SpawnObjectSize = 1024;

	[StructLayout(LayoutKind.Sequential)]
	internal struct Winsize {
		public ushort ws_row;
		public ushort ws_col;
		public ushort ws_xpixel;
		public ushort ws_ypixel;
	}

	[LibraryImport("libc", SetLastError = true)]
	internal static partial int posix_openpt(int flags);

	[LibraryImport("libc", SetLastError = true)]
	internal static partial int grantpt(int fd);

	[LibraryImport("libc", SetLastError = true)]
	internal static partial int unlockpt(int fd);

	[LibraryImport("libc", SetLastError = true)]
	internal static partial IntPtr ptsname(int fd);

	// posix_spawn_file_actions_t / posix_spawnattr_t are passed as a pointer to caller-owned storage.
	// On macOS these types are opaque pointers (the storage holds a single malloc'd handle); on glibc they
	// are sizeable structs initialized in place. Either way the ABI is "pointer to the object", so these
	// take the buffer pointer by value — the caller (PosixPtyTerminal) allocates storage large enough for
	// the platform's struct. Passing `ref IntPtr` (an 8-byte slot) would let glibc's *_init overflow the
	// stack writing its full struct ("stack smashing detected").
	[LibraryImport("libc", SetLastError = true)]
	internal static partial int posix_spawn_file_actions_init(IntPtr fileActions);

	[LibraryImport("libc", SetLastError = true)]
	internal static partial int posix_spawn_file_actions_destroy(IntPtr fileActions);

	[LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial int posix_spawn_file_actions_addopen(
		IntPtr fileActions, int filedes, string path, int oflag, uint mode);

	[LibraryImport("libc", SetLastError = true)]
	internal static partial int posix_spawn_file_actions_adddup2(
		IntPtr fileActions, int filedes, int newfiledes);

	[LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial int posix_spawn_file_actions_addchdir_np(IntPtr fileActions, string path);

	[LibraryImport("libc", SetLastError = true)]
	internal static partial int posix_spawnattr_init(IntPtr attr);

	[LibraryImport("libc", SetLastError = true)]
	internal static partial int posix_spawnattr_destroy(IntPtr attr);

	[LibraryImport("libc", SetLastError = true)]
	internal static partial int posix_spawnattr_setflags(IntPtr attr, short flags);

	[LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial int posix_spawn(
		out int pid, string path, IntPtr fileActions, IntPtr attr, IntPtr argv, IntPtr envp);

	[LibraryImport("libc", SetLastError = true)]
	internal static partial nint read(int fd, byte[] buffer, nuint count);

	[LibraryImport("libc", SetLastError = true)]
	internal static partial nint write(int fd, byte[] buffer, nuint count);

	[LibraryImport("libc", SetLastError = true)]
	internal static partial int close(int fd);

	[LibraryImport("libc", SetLastError = true)]
	internal static partial int fcntl(int fd, int cmd, int arg);

	[LibraryImport("libc", SetLastError = true)]
	internal static partial int ioctl(int fd, nuint request, ref Winsize winsize);

	[LibraryImport("libc", SetLastError = true)]
	internal static partial int waitpid(int pid, out int status, int options);

	[LibraryImport("libc", SetLastError = true)]
	internal static partial int kill(int pid, int sig);
}
