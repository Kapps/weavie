// macOS PTY operations stay native so variadic ioctl follows the Apple Silicon ABI.
#include <sys/ioctl.h>
#include <sys/wait.h>
#include <termios.h>
#include <util.h>
#include <unistd.h>
#include <fcntl.h>
#include <errno.h>
#include <signal.h>
#include <spawn.h>
#include <stdlib.h>
#include "weavie_spawn.h"

int weavie_set_winsize(int fd, unsigned short rows, unsigned short cols) {
	struct winsize ws = { .ws_row = rows, .ws_col = cols };
	return ioctl(fd, TIOCSWINSZ, &ws);
}

static int await_exec(int fd) {
	int error = 0;
	size_t received = 0;
	while (received < sizeof(error)) {
		ssize_t count = read(fd, (char *)&error + received, sizeof(error) - received);
		if (count < 0) {
			if (errno == EINTR) continue;
			return errno;
		}
		if (count == 0) return received == 0 ? 0 : EIO;
		received += (size_t)count;
	}
	return error == 0 ? EIO : error;
}

// The host never forks: only a fresh executable may acquire the child's controlling terminal.
// Returns a negative errno on failure; ownership transfers only on success.
int weavie_pty_spawn(const char *launcher,
                     char *const argv[],
                     char *const envp[],
                     const char *cwd,
                     unsigned short rows,
                     unsigned short cols,
                     int *out_master,
                     int *out_pid) {
	int master = -1, slave = -1, status[2] = { -1, -1 };
	int error = 0;
	pid_t pid = -1;
	struct winsize ws = { .ws_row = rows, .ws_col = cols };
	if (openpty(&master, &slave, NULL, NULL, &ws) != 0) return -errno;
	master = weavie_own_fd(master);
	if (master < 0) { error = errno; goto cleanup; }
	slave = weavie_own_fd(slave);
	if (slave < 0) { error = errno; goto cleanup; }
	if (pipe(status) != 0) { error = errno; goto cleanup; }
	status[0] = weavie_own_fd(status[0]);
	if (status[0] < 0) { error = errno; goto cleanup; }
	status[1] = weavie_own_fd(status[1]);
	if (status[1] < 0) { error = errno; goto cleanup; }

	size_t argc = 0;
	while (argv[argc] != NULL) argc++;
	char **args = calloc(argc + 2, sizeof(char *));
	if (args == NULL) { error = ENOMEM; goto cleanup; }
	args[0] = (char *)launcher;
	for (size_t i = 0; i < argc; i++) args[i + 1] = argv[i];

	int fds[] = { slave, slave, slave, status[1] };
	error = weavie_spawn_isolated(launcher, args, envp, cwd, fds, 4, &pid);
	free(args);
	if (error != 0) goto cleanup;
	close(slave);
	slave = -1;
	close(status[1]);
	status[1] = -1;
	error = await_exec(status[0]);
	if (error != 0) {
		kill(pid, SIGKILL);
		while (waitpid(pid, NULL, 0) < 0 && errno == EINTR) {}
		goto cleanup;
	}
	*out_master = master;
	*out_pid = pid;
	master = -1;
cleanup:
	if (master >= 0) close(master);
	if (slave >= 0) close(slave);
	if (status[0] >= 0) close(status[0]);
	if (status[1] >= 0) close(status[1]);
	return -error;
}
