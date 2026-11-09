/* Test probe for test-bpf-vm.sh: open or list a path, spawn a child with an empty environment, or delay. */
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/wait.h>
#include <unistd.h>

static int run(int argc, char **argv)
{
    if (argc >= 2 && strcmp(argv[0], "open") == 0)
    {
        int fd = open(argv[1], O_RDONLY | O_NONBLOCK);
        if (fd < 0)
        {
            fprintf(stderr, "open %s: %s\n", argv[1], strerror(errno));
            return 1;
        }
        close(fd);
        return 0;
    }
    if (argc >= 3 && strcmp(argv[0], "list") == 0)
    {
        DIR *d = opendir(argv[1]);
        struct dirent *e;
        int found = 0;
        while (d && (e = readdir(d)))
            found |= strcmp(e->d_name, argv[2]) == 0;
        if (d)
            closedir(d);
        return found ? 0 : 1;
    }
    if (argc >= 2 && strcmp(argv[0], "spawn") == 0)
    {
        pid_t pid = fork();
        if (pid == 0)
        {
            char *env[] = {NULL};
            execve(argv[1], argv + 1, env);
            _exit(127);
        }
        int st = 0;
        waitpid(pid, &st, 0);
        return WIFEXITED(st) ? WEXITSTATUS(st) : 1;
    }
    if (argc >= 3 && strcmp(argv[0], "delay") == 0)
    {
        sleep((unsigned)atoi(argv[1]));
        return run(argc - 2, argv + 2);
    }
    fprintf(stderr, "usage: probe open|list|spawn|delay ...\n");
    return 2;
}

int main(int argc, char **argv)
{
    return run(argc - 1, argv + 1);
}
