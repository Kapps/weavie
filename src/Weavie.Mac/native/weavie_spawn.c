#include "weavie_spawn.h"
#include <spawn.h>
#include <signal.h>
#include <fcntl.h>
#include <errno.h>
#include <unistd.h>

// Keep sources above stdio and the PTY launcher's status descriptor (3).
int weavie_own_fd(int fd) {
	int owned = fcntl(fd, F_DUPFD_CLOEXEC, 4);
	int error = errno;
	close(fd);
	errno = error;
	return owned;
}

int weavie_spawn_isolated(const char *path, char *const argv[], char *const envp[],
                         const char *cwd, const int *fds, int count, pid_t *pid) {
	posix_spawn_file_actions_t actions;
	posix_spawnattr_t attr;
	int error = posix_spawn_file_actions_init(&actions);
	if (error != 0) return error;
	error = posix_spawnattr_init(&attr);
	if (error != 0) goto destroy_actions;
	sigset_t defaults, mask;
	sigfillset(&defaults);
	sigemptyset(&mask);
	if ((error = posix_spawnattr_setsigdefault(&attr, &defaults)) != 0) goto destroy_attr;
	if ((error = posix_spawnattr_setsigmask(&attr, &mask)) != 0) goto destroy_attr;
	if ((error = posix_spawnattr_setflags(&attr, POSIX_SPAWN_SETSID | POSIX_SPAWN_CLOEXEC_DEFAULT |
		POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK)) != 0) goto destroy_attr;
	for (int fd = 0; fd < count; fd++) {
		if ((error = posix_spawn_file_actions_adddup2(&actions, fds[fd], fd)) != 0) goto destroy_attr;
	}
	if (cwd != NULL && cwd[0] != '\0' &&
		(error = posix_spawn_file_actions_addchdir_np(&actions, cwd)) != 0) goto destroy_attr;
	error = posix_spawnp(pid, path, &actions, &attr, argv, envp);
destroy_attr:
	posix_spawnattr_destroy(&attr);
destroy_actions:
	posix_spawn_file_actions_destroy(&actions);
	return error;
}
