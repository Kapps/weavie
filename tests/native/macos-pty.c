#include <sys/wait.h>
#include <sys/ioctl.h>
#include <assert.h>
#include <stdbool.h>
#include <errno.h>
#include <fcntl.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

extern char **environ;
int weavie_pty_spawn(const char *, char *const [], char *const [], const char *,
                    unsigned short, unsigned short, int *, int *);

static volatile sig_atomic_t parent_signal;
static void observe(int signal) { parent_signal = signal; }

static int reap(int pid) {
	int status;
	int result;
	do { result = waitpid(pid, &status, 0); } while (result < 0 && errno == EINTR);
	assert(result == pid);
	return status;
}

// This executable is also the target, so the assertions inspect the exact exec boundary.
static int inspect_child(int sentinel) {
	assert(getsid(0) == getpid());
	assert(getpgrp() == getpid());
	assert(tcgetpgrp(0) == getpid());
	assert(isatty(0) && isatty(1) && isatty(2));
	assert(fcntl(sentinel, F_GETFD) == -1 && errno == EBADF);
	assert(fcntl(3, F_GETFD) == -1 && errno == EBADF);
	struct winsize ws;
	assert(ioctl(0, TIOCGWINSZ, &ws) == 0);
	assert(ws.ws_row == 37 && ws.ws_col == 113);
	sigset_t mask;
	assert(sigprocmask(SIG_SETMASK, NULL, &mask) == 0);
	assert(sigismember(&mask, SIGUSR1) == 0);
	struct sigaction action;
	assert(sigaction(SIGUSR2, NULL, &action) == 0);
	assert(action.sa_handler == SIG_DFL);
	return 0;
}

static const char output_marker[] = "retained terminal output";

static int close_output_child(void) {
	assert(write(STDOUT_FILENO, output_marker, sizeof(output_marker)) == sizeof(output_marker));
	assert(close(STDIN_FILENO) == 0);
	assert(close(STDOUT_FILENO) == 0);
	assert(close(STDERR_FILENO) == 0);
	assert(raise(SIGSTOP) == 0);
	return 0;
}

static void retain_output_after_stdio_close(const char *launcher, char *executable) {
	char *child[] = { executable, "close-output", NULL };
	int master, pid, status, result;
	assert(weavie_pty_spawn(launcher, child, environ, NULL, 37, 113, &master, &pid) == 0);
	do { result = waitpid(pid, &status, WUNTRACED); } while (result < 0 && errno == EINTR);
	assert(result == pid && WIFSTOPPED(status));
	int flags = fcntl(master, F_GETFL);
	assert(flags >= 0 && fcntl(master, F_SETFL, flags | O_NONBLOCK) == 0);
	char output[sizeof(output_marker)];
	size_t received = 0;
	while (received < sizeof(output)) {
		ssize_t count = read(master, output + received, sizeof(output) - received);
		if (count < 0 && errno == EINTR) continue;
		if (count <= 0) break;
		received += (size_t)count;
	}
	assert(kill(pid, SIGCONT) == 0);
	assert(fcntl(master, F_SETFL, flags) == 0);
	char remaining[256];
	while (true) {
		ssize_t count = read(master, remaining, sizeof(remaining));
		if (count > 0 || (count < 0 && errno == EINTR)) continue;
		break;
	}
	status = reap(pid);
	close(master);
	assert(WIFEXITED(status) && WEXITSTATUS(status) == 0);
	assert(received == sizeof(output) && memcmp(output, output_marker, sizeof(output)) == 0);
}

int main(int argc, char **argv) {
	if (argc == 2 && strcmp(argv[1], "close-output") == 0) return close_output_child();
	if (argc == 3 && strcmp(argv[1], "child") == 0) return inspect_child(atoi(argv[2]));
	assert(argc == 2);
	const char *launcher = argv[1];
	pid_t group = getpgrp();
	struct sigaction action = { .sa_handler = observe };
	assert(sigaction(SIGHUP, &action, NULL) == 0);
	assert(sigaction(SIGTERM, &action, NULL) == 0);
	action.sa_handler = SIG_IGN;
	assert(sigaction(SIGUSR2, &action, NULL) == 0);
	sigset_t mask;
	sigemptyset(&mask);
	sigaddset(&mask, SIGUSR1);
	assert(sigprocmask(SIG_BLOCK, &mask, NULL) == 0);

	int source = open("/dev/null", O_RDONLY);
	assert(source >= 0);
	int sentinel = fcntl(source, F_DUPFD, 100);
	assert(sentinel >= 100);
	close(source);
	char descriptor[32];
	snprintf(descriptor, sizeof(descriptor), "%d", sentinel);
	char *child[] = { argv[0], "child", descriptor, NULL };
	int master = -1, pid = -1;
	assert(weavie_pty_spawn(launcher, child, environ, NULL, 37, 113, &master, &pid) == 0);
	int status = reap(pid);
	assert(WIFEXITED(status) && WEXITSTATUS(status) == 0);
	close(master);
	close(sentinel);
	retain_output_after_stdio_close(launcher, argv[0]);

	char *missing[] = { "/weavie-missing-executable", NULL };
	assert(weavie_pty_spawn(launcher, missing, environ, NULL, 37, 113, &master, &pid) == -ENOENT);
	assert(weavie_pty_spawn(launcher, child, environ, "/weavie-missing-directory", 37, 113,
		&master, &pid) == -ENOENT);
	assert(weavie_pty_spawn("/weavie-missing-launcher", child, environ, NULL, 37, 113,
		&master, &pid) == -ENOENT);

	// Immediate repeated teardown exercises startup/disposal without waiting for an agent to become ready.
	char *running[] = { "/bin/sleep", "60", NULL };
	for (int i = 0; i < 32; i++) {
		assert(weavie_pty_spawn(launcher, running, environ, NULL, 37, 113, &master, &pid) == 0);
		assert(pid > 0 && pid != getpid() && pid != group);
		assert(getpgid(pid) == pid);
		assert(kill(-pid, SIGTERM) == 0);
		status = reap(pid);
		assert(WIFSIGNALED(status) && WTERMSIG(status) == SIGTERM);
		close(master);
		assert(parent_signal == 0 && getpgrp() == group);
	}
	puts("PASS: controlling terminal, descriptors, signals, launch failures, repeated teardown");
	return 0;
}
