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

int weavie_set_winsize(int fd, unsigned short rows, unsigned short cols) {
	struct winsize ws = { .ws_row = rows, .ws_col = cols };
	return ioctl(fd, TIOCSWINSZ, &ws);
}

// Keep spawn-action sources above stdio and the launcher's status descriptor (3).
static int own_fd(int fd) {
	int owned = fcntl(fd, F_DUPFD_CLOEXEC, 4);
	int error = errno;
	close(fd);
	errno = error;
	return owned;
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
	master = own_fd(master);
	if (master < 0) { error = errno; goto cleanup; }
	slave = own_fd(slave);
	if (slave < 0) { error = errno; goto cleanup; }
	if (pipe(status) != 0) { error = errno; goto cleanup; }
	status[0] = own_fd(status[0]);
	if (status[0] < 0) { error = errno; goto cleanup; }
	status[1] = own_fd(status[1]);
	if (status[1] < 0) { error = errno; goto cleanup; }

	size_t argc = 0;
	while (argv[argc] != NULL) argc++;
	char **args = calloc(argc + 2, sizeof(char *));
	if (args == NULL) { error = ENOMEM; goto cleanup; }
	args[0] = (char *)launcher;
	for (size_t i = 0; i < argc; i++) args[i + 1] = argv[i];

	posix_spawn_file_actions_t actions;
	posix_spawnattr_t attr;
	error = posix_spawn_file_actions_init(&actions);
	if (error != 0) goto free_args;
	error = posix_spawnattr_init(&attr);
	if (error != 0) goto destroy_actions;

	sigset_t defaults, mask;
	sigfillset(&defaults);
	sigemptyset(&mask);
	if ((error = posix_spawnattr_setsigdefault(&attr, &defaults)) != 0) goto destroy_attr;
	if ((error = posix_spawnattr_setsigmask(&attr, &mask)) != 0) goto destroy_attr;
	if ((error = posix_spawnattr_setflags(&attr, POSIX_SPAWN_SETSID | POSIX_SPAWN_CLOEXEC_DEFAULT |
		POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK)) != 0) goto destroy_attr;
	for (int fd = 0; fd < 3; fd++) {
		if ((error = posix_spawn_file_actions_adddup2(&actions, slave, fd)) != 0) goto destroy_attr;
	}
	if ((error = posix_spawn_file_actions_adddup2(&actions, status[1], 3)) != 0) goto destroy_attr;
	if (cwd != NULL && cwd[0] != '\0' &&
		(error = posix_spawn_file_actions_addchdir_np(&actions, cwd)) != 0) goto destroy_attr;
	error = posix_spawn(&pid, launcher, &actions, &attr, args, envp);

destroy_attr:
	posix_spawnattr_destroy(&attr);
destroy_actions:
	posix_spawn_file_actions_destroy(&actions);
free_args:
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
