using System.Runtime.InteropServices;

namespace Weavie.Core.Terminal;

/// <summary>
/// libc P/Invoke for a POSIX PTY (macOS + Linux). Linux spawns the child via <c>posix_spawn</c> +
/// <c>POSIX_SPAWN_SETSID</c>, which acquires the slave tty as controlling terminal by opening it after setsid;
/// macOS can't grant the ctty that way (no <c>posix_spawn</c> file-action for <c>TIOCSCTTY</c>) so it uses the
/// native <c>weavie_pty_spawn</c> (forkpty) shim instead. Kernel-differing constants are selected at runtime.
/// </summary>
internal static partial class NativeMethods {
	// open(2) flags. O_RDWR is the same on both; O_NOCTTY differs (macOS 0x20000, Linux 0400).
	internal const int O_RDWR = 0x0002;
	internal static readonly int O_NOCTTY = OperatingSystem.IsMacOS() ? 0x20000 : 0x100;

	// posix_spawn attribute flags. POSIX_SPAWN_SETSID differs (macOS 0x0400, glibc 0x80); CLOEXEC_DEFAULT is
	// Apple-only (Linux makes the master close-on-exec via fcntl in PosixPtyTerminal.Start instead).
	internal static readonly short POSIX_SPAWN_SETSID = (short)(OperatingSystem.IsMacOS() ? 0x0400 : 0x80);
	internal const short POSIX_SPAWN_CLOEXEC_DEFAULT = 0x4000;

	// fcntl(2) for the Linux close-on-exec path described above.
	internal const int F_SETFD = 2;
	internal const int FD_CLOEXEC = 1;

	// Child-teardown signals (same numbers on both kernels), sent to the negated pid to reach the whole
	// process group (see PosixPtyTerminal.Dispose).
	internal const int SIGTERM = 15;
	internal const int SIGKILL = 9;

	// ioctl request: TIOCSWINSZ = _IOW('t', 103, struct winsize) on macOS; fixed 0x5414 on Linux.
	internal static readonly nuint TIOCSWINSZ = OperatingSystem.IsMacOS() ? 0x80087467 : 0x5414;

	// Caller-allocated storage for posix_spawn_file_actions_t / posix_spawnattr_t, oversized vs. either
	// platform's struct (glibc's is ~336 bytes; macOS an 8-byte pointer) so *_init can't overrun the buffer.
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

	// These take the object pointer by value (pointer to caller-owned storage); passing `ref IntPtr` (an 8-byte
	// slot) would let glibc's *_init overflow the stack ("stack smashing detected").
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

	// macOS-only resize shim (libweavie_pty.dylib). libc's ioctl is variadic; a fixed-signature P/Invoke on
	// arm64-apple passes the winsize pointer in a register, dropping TIOCSWINSZ and blanking a TUI at 0x0.
	// Issuing it from C lays the variadic call out correctly. See native/weavie_pty.c.
	[LibraryImport("libweavie_pty", SetLastError = true)]
	internal static partial int weavie_set_winsize(int fd, ushort rows, ushort cols);

	// macOS-only PTY spawn shim (libweavie_pty.dylib): forkpty + login_tty + execve, giving the child a
	// controlling terminal (interactive shells like nushell require it). macOS has no posix_spawn file-action
	// for TIOCSCTTY, so this must run in C (fork() is unsafe from the managed runtime). argv/envp are
	// NULL-terminated char** (pinned); cwd may be null. Returns 0 + master fd/pid, or a negative errno.
	// See native/weavie_pty.c.
	[LibraryImport("libweavie_pty", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial int weavie_pty_spawn(
		string path, IntPtr argv, IntPtr envp, string? cwd, ushort rows, ushort cols, out int master, out int pid);

	// Foreground process group of the pty (glibc/libSystem implement this as ioctl(TIOCGPGRP), which
	// works on the master fd). Compared against the child's pgid for the drain gate's job probe.
	[LibraryImport("libc", SetLastError = true)]
	internal static partial int tcgetpgrp(int fd);

	[LibraryImport("libc", SetLastError = true)]
	internal static partial int waitpid(int pid, out int status, int options);

	[LibraryImport("libc", SetLastError = true)]
	internal static partial int kill(int pid, int sig);
}
