using System.Runtime.InteropServices;

namespace Weavie.Core.Terminal;

/// <summary>
/// libc P/Invoke for a POSIX PTY (macOS + Linux). On Linux we open a pseudo-terminal master and launch
/// the child via <c>posix_spawn</c> with <c>POSIX_SPAWN_SETSID</c> — the child becomes a session leader
/// and, because the spawn file-actions open the slave tty (no O_NOCTTY) after setsid, acquires it as its
/// controlling terminal. macOS does <em>not</em> grant the controlling tty that way (BSD semantics need
/// an explicit <c>ioctl(TIOCSCTTY)</c>, which has no <c>posix_spawn</c> file-action), so there the child
/// is spawned through the native <c>weavie_pty_spawn</c> (forkpty) shim instead — without a controlling
/// terminal interactive shells like nushell abort with "no TTY for interactive shell". Either way the
/// runtime never calls <c>fork()</c> itself (unsafe with the .NET thread pool); only async-signal-safe
/// work runs in the child. The few constants that differ between the two kernels are selected at runtime.
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

	// Signals for child teardown (same numbers on macOS and Linux). SIGTERM asks the child to exit cleanly;
	// SIGKILL is the non-catchable force-kill we escalate to. Sent to the negated pid so they reach the
	// child's whole process group (see PosixPtyTerminal.Dispose).
	internal const int SIGTERM = 15;
	internal const int SIGKILL = 9;

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

	// macOS-only resize shim (libweavie_pty.dylib, bundled next to the managed assemblies). libc's ioctl is
	// variadic, and on arm64-apple variadic arguments are passed on the stack — a fixed-signature P/Invoke
	// passes the winsize pointer in a register, so the kernel reads garbage and the TIOCSWINSZ is dropped,
	// leaving a full-screen TUI (claude) stuck at a 0x0 size and rendering blank. Issuing the ioctl from C
	// lays the variadic call out correctly. Resolved only on macOS, with a managed-ioctl fallback when the
	// dylib isn't on the load path (unit tests run outside the .app bundle). See native/weavie_pty.c.
	[LibraryImport("libweavie_pty", SetLastError = true)]
	internal static partial int weavie_set_winsize(int fd, ushort rows, ushort cols);

	// macOS-only PTY spawn shim (libweavie_pty.dylib). forkpty(3) + execve(2): the child gets the slave as
	// its controlling terminal (login_tty -> setsid + TIOCSCTTY + dup onto stdin/out/err), which interactive
	// shells like nushell require ("no TTY for interactive shell" otherwise). macOS, unlike Linux, doesn't
	// grant the controlling tty by opening the slave after setsid, and there's no posix_spawn file-action for
	// TIOCSCTTY — so the fork+login_tty+exec must run in C (fork() is unsafe from the managed runtime). argv
	// and envp are NULL-terminated char** (pinned managed arrays); cwd may be null. Returns 0 and writes the
	// master fd + pid, or a negative errno. Resolved only on macOS; a DllNotFoundException (tests run outside
	// the .app bundle) falls back to the managed posix_spawn path, adequate for the non-interactive children
	// tests spawn. See native/weavie_pty.c.
	[LibraryImport("libweavie_pty", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial int weavie_pty_spawn(
		string path, IntPtr argv, IntPtr envp, string? cwd, ushort rows, ushort cols, out int master, out int pid);

	[LibraryImport("libc", SetLastError = true)]
	internal static partial int waitpid(int pid, out int status, int options);

	[LibraryImport("libc", SetLastError = true)]
	internal static partial int kill(int pid, int sig);
}
