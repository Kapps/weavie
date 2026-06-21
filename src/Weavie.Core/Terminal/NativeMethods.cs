using System.Runtime.InteropServices;

namespace Weavie.Core.Terminal;

/// <summary>
/// libc P/Invoke for a POSIX PTY (macOS + Linux). On Linux the child is spawned via <c>posix_spawn</c> with
/// <c>POSIX_SPAWN_SETSID</c>, acquiring the slave tty as its controlling terminal by opening it (no O_NOCTTY)
/// after setsid. macOS doesn't grant the ctty that way (BSD needs <c>ioctl(TIOCSCTTY)</c>, with no
/// <c>posix_spawn</c> file-action), so there the child is spawned through the native <c>weavie_pty_spawn</c>
/// (forkpty) shim — interactive shells like nushell abort with "no TTY for interactive shell" otherwise. The
/// runtime never calls <c>fork()</c> itself (unsafe with the .NET thread pool). Constants that differ between
/// the two kernels are selected at runtime.
/// </summary>
internal static partial class NativeMethods {
	// open(2) flags. O_RDWR is the same on both; O_NOCTTY differs (macOS 0x20000, Linux 0400).
	internal const int O_RDWR = 0x0002;
	internal static readonly int O_NOCTTY = OperatingSystem.IsMacOS() ? 0x20000 : 0x100;

	// posix_spawn attribute flags. POSIX_SPAWN_SETSID differs (macOS 0x0400, glibc 0x80).
	// POSIX_SPAWN_CLOEXEC_DEFAULT is Apple-only; on Linux the master fd is made close-on-exec via fcntl
	// (see PosixPtyTerminal.Start) so it never leaks into the spawned child.
	internal static readonly short POSIX_SPAWN_SETSID = (short)(OperatingSystem.IsMacOS() ? 0x0400 : 0x80);
	internal const short POSIX_SPAWN_CLOEXEC_DEFAULT = 0x4000;

	// fcntl(2) for the Linux close-on-exec path described above.
	internal const int F_SETFD = 2;
	internal const int FD_CLOEXEC = 1;

	// Signals for child teardown (same numbers on both kernels). SIGTERM for a clean exit, SIGKILL the
	// force-kill we escalate to. Sent to the negated pid to reach the child's whole process group (see
	// PosixPtyTerminal.Dispose).
	internal const int SIGTERM = 15;
	internal const int SIGKILL = 9;

	// ioctl request: TIOCSWINSZ = _IOW('t', 103, struct winsize) on macOS; fixed 0x5414 on Linux.
	internal static readonly nuint TIOCSWINSZ = OperatingSystem.IsMacOS() ? 0x80087467 : 0x5414;

	// Caller-allocated storage size for posix_spawn_file_actions_t / posix_spawnattr_t. Generously larger than
	// either platform's struct (glibc's posix_spawnattr_t is ~336 bytes; macOS an 8-byte opaque pointer) so
	// *_init can initialize the object in place without overrunning the buffer.
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

	// posix_spawn_file_actions_t / posix_spawnattr_t are passed as a pointer to caller-owned storage (an
	// opaque pointer on macOS, a sizeable struct initialized in place on glibc). The ABI is "pointer to the
	// object", so these take the buffer pointer by value — passing `ref IntPtr` (an 8-byte slot) would let
	// glibc's *_init overflow the stack ("stack smashing detected").
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

	// macOS-only resize shim (libweavie_pty.dylib). libc's ioctl is variadic, and on arm64-apple a
	// fixed-signature P/Invoke passes the winsize pointer in a register instead of on the stack, so the
	// TIOCSWINSZ is dropped and a full-screen TUI (claude) renders blank at 0x0. Issuing it from C lays the
	// variadic call out correctly. Falls back to managed ioctl when the dylib isn't on the load path (tests
	// run outside the .app bundle). See native/weavie_pty.c.
	[LibraryImport("libweavie_pty", SetLastError = true)]
	internal static partial int weavie_set_winsize(int fd, ushort rows, ushort cols);

	// macOS-only PTY spawn shim (libweavie_pty.dylib). forkpty(3) + execve(2): the child gets the slave as its
	// controlling terminal (login_tty), which interactive shells like nushell require. macOS doesn't grant the
	// ctty by opening the slave after setsid and has no posix_spawn file-action for TIOCSCTTY, so the
	// fork+login_tty+exec must run in C (fork() is unsafe from the managed runtime). argv/envp are
	// NULL-terminated char** (pinned managed arrays); cwd may be null. Returns 0 and writes the master fd +
	// pid, or a negative errno. Falls back to the managed posix_spawn path on DllNotFoundException (tests run
	// outside the .app bundle). See native/weavie_pty.c.
	[LibraryImport("libweavie_pty", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial int weavie_pty_spawn(
		string path, IntPtr argv, IntPtr envp, string? cwd, ushort rows, ushort cols, out int master, out int pid);

	[LibraryImport("libc", SetLastError = true)]
	internal static partial int waitpid(int pid, out int status, int options);

	[LibraryImport("libc", SetLastError = true)]
	internal static partial int kill(int pid, int sig);
}
