#include "weavie_spawn.h"
#include <sys/wait.h>
#include <errno.h>
#include <fcntl.h>
#include <unistd.h>

int weavie_process_spawn(const char *path, char *const argv[], char *const envp[], const char *cwd,
                         int *input, int *output, int *error_output, int *child_pid) {
	int pipes[6] = { -1, -1, -1, -1, -1, -1 };
	int error = 0;
	for (int i = 0; i < 6; i += 2) {
		if (pipe(&pipes[i]) != 0) { error = errno; goto cleanup; }
		for (int j = i; j < i + 2; j++) {
			pipes[j] = weavie_own_fd(pipes[j]);
			if (pipes[j] < 0) { error = errno; goto cleanup; }
		}
	}
	if (fcntl(pipes[1], F_SETNOSIGPIPE, 1) < 0) { error = errno; goto cleanup; }
	int fds[] = { pipes[0], pipes[3], pipes[5] };
	pid_t pid;
	error = weavie_spawn_isolated(path, argv, envp, cwd, fds, 3, &pid);
	if (error != 0) goto cleanup;
	*input = pipes[1]; pipes[1] = -1;
	*output = pipes[2]; pipes[2] = -1;
	*error_output = pipes[4]; pipes[4] = -1;
	*child_pid = pid;
cleanup:
	for (int i = 0; i < 6; i++) if (pipes[i] >= 0) close(pipes[i]);
	return -error;
}

// Leave the zombie owned until managed code holds the same gate used for signaling.
int weavie_process_wait(int pid) {
	siginfo_t info;
	int result;
	do { result = waitid(P_PID, (id_t)pid, &info, WEXITED | WNOWAIT); }
	while (result < 0 && errno == EINTR);
	return result < 0 ? -errno : 0;
}

int weavie_process_reap(int pid, int *code) {
	int status, result;
	do { result = waitpid(pid, &status, 0); } while (result < 0 && errno == EINTR);
	if (result < 0) return -errno;
	*code = WIFEXITED(status) ? WEXITSTATUS(status) : 128 + WTERMSIG(status);
	return 0;
}
