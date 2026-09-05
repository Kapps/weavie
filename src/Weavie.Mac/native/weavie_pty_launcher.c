#include <sys/ioctl.h>
#include <unistd.h>
#include <fcntl.h>
#include <errno.h>

// Descriptor 3 is the parent's launch-status pipe, closed atomically by successful exec.
static void fail(int error) {
	const char *bytes = (const char *)&error;
	size_t remaining = sizeof(error);
	while (remaining > 0) {
		ssize_t count = write(3, bytes, remaining);
		if (count < 0 && errno == EINTR) continue;
		if (count <= 0) break;
		bytes += count;
		remaining -= (size_t)count;
	}
	_exit(127);
}

int main(int argc, char **argv, char **envp) {
	if (argc < 2) fail(EINVAL);
	if (fcntl(3, F_SETFD, FD_CLOEXEC) < 0) fail(errno);
	if (ioctl(0, TIOCSCTTY, 0) < 0) fail(errno);
	execve(argv[1], &argv[1], envp);
	fail(errno);
}
