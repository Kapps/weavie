// Tiny native shim for the two PTY operations managed code cannot do safely on Apple Silicon / macOS.
//
// (1) weavie_set_winsize — resize a tty. ioctl(fd, TIOCSWINSZ, &winsize) goes through libc's variadic
//     ioctl (int ioctl(int, unsigned long, ...)); on arm64-apple variadic arguments are passed on the
//     stack, but a fixed-signature P/Invoke hands the pointer in a register, so the kernel reads a
//     garbage pointer and the resize is dropped (a full-screen TUI like claude stays blank at 0x0).
//     C#'s only varargs syntax (__arglist) is rejected by CoreCLR on arm64. Issuing the ioctl from C
//     lays the variadic call out correctly.
//
// (2) weavie_pty_spawn — launch a child in a fresh PTY whose slave is the child's CONTROLLING terminal.
//     Interactive shells (nushell, fish, …) abort with "no TTY for interactive shell" without one. On
//     macOS, unlike Linux, a session leader does NOT acquire the controlling tty merely by opening the
//     slave after setsid — it needs ioctl(TIOCSCTTY), and there is no posix_spawn file-action for that.
//     forkpty(3)'s login_tty() does setsid + TIOCSCTTY + dup the slave onto stdin/stdout/stderr in one
//     shot. We can't call fork() from the managed runtime (only the calling thread survives the fork and
//     almost nothing is async-signal-safe afterwards), so the fork+login_tty+exec sequence is issued
//     here in C, where only async-signal-safe calls run between fork and exec.
#include <sys/ioctl.h>
#include <sys/types.h>
#include <termios.h>
#include <util.h>
#include <unistd.h>
#include <fcntl.h>
#include <errno.h>

int weavie_set_winsize(int fd, unsigned short rows, unsigned short cols) {
	struct winsize ws;
	ws.ws_row = rows;
	ws.ws_col = cols;
	ws.ws_xpixel = 0;
	ws.ws_ypixel = 0;
	return ioctl(fd, TIOCSWINSZ, &ws);
}

// Spawns `path` (argv[0] should equal it; neither this nor posix_spawn searches PATH) in a new PTY,
// with `envp` as the entire environment and `cwd` as the working directory (NULL/empty = inherit).
// argv and envp are NULL-terminated char** arrays. On success returns 0 and writes the master fd and
// child pid; on failure returns a negative errno and writes neither output.
int weavie_pty_spawn(const char *path,
                     char *const argv[],
                     char *const envp[],
                     const char *cwd,
                     unsigned short rows,
                     unsigned short cols,
                     int *out_master,
                     int *out_pid) {
	struct winsize ws;
	ws.ws_row = rows;
	ws.ws_col = cols;
	ws.ws_xpixel = 0;
	ws.ws_ypixel = 0;

	int master = -1;
	pid_t pid = forkpty(&master, NULL, NULL, &ws);
	if (pid < 0) {
		return -errno;
	}

	if (pid == 0) {
		// Child. forkpty already closed the master, called setsid(), made the slave our controlling
		// terminal, and dup'd it onto stdin/stdout/stderr. Only chdir + exec remain — both
		// async-signal-safe, the one constraint on the post-fork child.
		if (cwd != NULL && cwd[0] != '\0' && chdir(cwd) != 0) {
			_exit(127);
		}
		execve(path, argv, envp);
		_exit(127); // exec failed
	}

	// Parent. Keep the master from leaking into any child we exec later (the next terminal); macOS's
	// openpty does not set close-on-exec on it.
	fcntl(master, F_SETFD, FD_CLOEXEC);
	*out_master = master;
	*out_pid = pid;
	return 0;
}
