/* Debug aid for test-bpf-vm.sh: load the filter keeping only the named programs, print the verifier log. */
#include <dlfcn.h>
#include <stdarg.h>
#include <stdbool.h>
#include <stdio.h>
#include <string.h>

static int pr(int level, const char *fmt, va_list ap)
{
    if (level == 2) /* LIBBPF_DEBUG */
        return 0;
    return vfprintf(stderr, fmt, ap);
}

int main(int argc, char **argv)
{
    void *h = dlopen("libbpf.so.1", RTLD_NOW);
    void (*set_print)(void *) = dlsym(h, "libbpf_set_print");
    void *(*open_file)(const char *, const void *) = dlsym(h, "bpf_object__open_file");
    void *(*next_prog)(const void *, void *) = dlsym(h, "bpf_object__next_program");
    const char *(*prog_name)(const void *) = dlsym(h, "bpf_program__name");
    int (*set_autoload)(void *, bool) = dlsym(h, "bpf_program__set_autoload");
    int (*load)(void *) = dlsym(h, "bpf_object__load");
    set_print(pr);
    void *obj = open_file(argv[1], NULL);
    for (void *p = next_prog(obj, NULL); p; p = next_prog(obj, p))
    {
        int keep = 0;
        for (int i = 2; i < argc; i++)
            keep |= strcmp(argv[i], prog_name(p)) == 0;
        set_autoload(p, keep);
    }
    int rc = load(obj);
    printf("load rc=%d\n", rc);
    return rc != 0;
}
