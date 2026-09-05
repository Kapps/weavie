#ifndef WEAVIE_SPAWN_H
#define WEAVIE_SPAWN_H
#include <sys/types.h>
int weavie_own_fd(int fd);
int weavie_spawn_isolated(const char *path, char *const argv[], char *const envp[],
                         const char *cwd, const int *fds, int count, pid_t *pid);
#endif
