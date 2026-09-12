#include <sys/wait.h>
#include <sys/ioctl.h>
#include <assert.h>
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

int main(int argc, char **argv) {
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
