/* lsm-enable adds bpf to the boot LSM list (active list + bpf); lsm-disable reverts only our change. */

#include "gpu-helper.h"
#include <sys/stat.h>

/* The test build (-DGH_LSM_TEST=dir) keeps every path in one scratch directory. */
#ifdef GH_LSM_TEST
#define LSM_PATH GH_LSM_TEST "/lsm"
#define GRUB_DEFAULT GH_LSM_TEST "/grub"
#define KERNEL_CMDLINE GH_LSM_TEST "/cmdline"
#define LSM_STATE GH_LSM_TEST "/state"
#define NIXOS_MARK GH_LSM_TEST "/NIXOS"
#else
#define LSM_PATH "/sys/kernel/security/lsm"
#define GRUB_DEFAULT "/etc/default/grub"
#define KERNEL_CMDLINE "/etc/kernel/cmdline"
#define LSM_STATE "/etc/ghelper/lsm-enable.state"
#define NIXOS_MARK "/etc/NIXOS"
#endif
#define GRUB_KEY "GRUB_CMDLINE_LINUX_DEFAULT"
#define LSM_BUF 1024

#define RC_REBOOT 5
#define RC_MANUAL 6

/* The lsm= token our edit replaced, written to the state file for lsm-disable. */
static char replaced_lsm[LSM_BUF + 8];

/* Read the active LSM list and append bpf. Returns 0 on failure. */
static int build_lsm_list(char *out, size_t out_size)
{
    char buf[LSM_BUF];
    int fd = open(LSM_PATH, O_RDONLY);
    if (fd < 0)
    {
        fprintf(stderr, "lsm-enable: cannot read %s: %s\n", LSM_PATH, strerror(errno));
        return 0;
    }
    ssize_t n = read(fd, buf, sizeof(buf) - 1);
    close(fd);
    if (n <= 0)
    {
        fprintf(stderr, "lsm-enable: %s is empty\n", LSM_PATH);
        return 0;
    }
    buf[n] = '\0';

    /* Strip trailing newline. */
    char *nl = strchr(buf, '\n');
    if (nl)
        *nl = '\0';

    /* Already enabled: nothing to do. */
    char check[LSM_BUF];
    snprintf(check, sizeof(check), "%s", buf);
    for (char *tok = strtok(check, ","); tok; tok = strtok(NULL, ","))
        if (strcmp(tok, "bpf") == 0)
        {
            fprintf(stderr, "lsm-enable: bpf is already active\n");
            return 0;
        }

    if (snprintf(out, out_size, "%s,bpf", buf) >= (int)out_size)
    {
        fprintf(stderr, "lsm-enable: LSM list too long\n");
        return 0;
    }
    return 1;
}

static int file_exists(const char *path)
{
    return access(path, F_OK) == 0;
}

static int have_tool(const char *tool)
{
#ifdef GH_LSM_TEST
    return strcmp(tool, "update-grub") == 0;
#endif
    const char *dirs[] = {"/usr/sbin", "/usr/bin", "/sbin", "/bin", NULL};
    char path[PATH_BUF_SIZE];
    for (int i = 0; dirs[i]; i++)
    {
        snprintf(path, sizeof(path), "%s/%s", dirs[i], tool);
        if (access(path, X_OK) == 0)
            return 1;
    }
    return 0;
}

/* Run a command, returning its exit status (-1 when it could not run). */
static int run_status(char *const argv[])
{
    pid_t child = fork();
    if (child < 0)
        return -1;
    if (child == 0)
    {
        execvp(argv[0], argv);
        _exit(127);
    }
    int status = 0;
    if (waitpid(child, &status, 0) < 0)
        return -1;
    if (!WIFEXITED(status))
        return -1;
    return WEXITSTATUS(status);
}

static int copy_backup(const char *path)
{
    char bak[PATH_BUF_SIZE];
    snprintf(bak, sizeof(bak), "%s.ghelper-bak", path);

    int in = open(path, O_RDONLY);
    if (in < 0)
        return 0;
    int out = open(bak, O_WRONLY | O_CREAT | O_TRUNC, 0644);
    if (out < 0)
    {
        close(in);
        return 0;
    }
    char buf[4096];
    ssize_t n;
    int ok = 1;
    while ((n = read(in, buf, sizeof(buf))) > 0)
        if (write(out, buf, (size_t)n) != n)
        {
            ok = 0;
            break;
        }
    if (n < 0)
        ok = 0;
    close(in);
    close(out);
    if (ok)
        glog(LOG_INFO, "lsm-enable: backed up %s to %s", path, bak);
    return ok;
}

/* ---------- ostree / rpm-ostree (Bazzite, Fedora Atomic, bootc) ---------- */

static int try_ostree(const char *lsm)
{
    if (!file_exists("/run/ostree-booted") || !have_tool("rpm-ostree"))
        return 0;

    char arg[LSM_BUF + 8];
    snprintf(arg, sizeof(arg), "lsm=%s", lsm);
    /* append-if-missing keeps repeated runs idempotent. The last lsm= on the
     * cmdline wins, so an older entry left by the distro does not defeat it. */
    char *const argv[] = {"rpm-ostree", "kargs", "--append-if-missing", arg, NULL};

    glog(LOG_INFO, "lsm-enable: rpm-ostree kargs --append-if-missing %s", arg);
    int rc = run_status(argv);
    if (rc != 0)
    {
        fprintf(stderr, "lsm-enable: rpm-ostree kargs failed (exit %d)\n", rc);
        return -1;
    }
    printf("method=rpm-ostree\n");
    return 1;
}

/* ---------- grubby (Fedora, RHEL) ---------- */

static int try_grubby(const char *lsm)
{
    if (!have_tool("grubby"))
        return 0;

    char arg[LSM_BUF + 8];
    snprintf(arg, sizeof(arg), "lsm=%s", lsm);
    char *const argv[] = {"grubby", "--update-kernel=ALL", "--args", arg, NULL};

    /* grubby replaces an existing lsm= value; the booted cmdline shows it. */
    char cmdline[4096] = {0};
    FILE *pc = fopen("/proc/cmdline", "r");
    if (pc)
    {
        if (fgets(cmdline, sizeof(cmdline), pc))
            for (char *tok = strtok(cmdline, " \n"); tok; tok = strtok(NULL, " \n"))
                if (strncmp(tok, "lsm=", 4) == 0)
                    snprintf(replaced_lsm, sizeof(replaced_lsm), "%s", tok);
        fclose(pc);
    }

    glog(LOG_INFO, "lsm-enable: grubby --update-kernel=ALL --args %s", arg);
    int rc = run_status(argv);
    if (rc != 0)
    {
        fprintf(stderr, "lsm-enable: grubby failed (exit %d)\n", rc);
        return -1;
    }
    printf("method=grubby\n");
    return 1;
}

/* ---------- /etc/default/grub (Ubuntu, Debian, Arch, CachyOS) ---------- */

/* Rewrite GRUB_CMDLINE_LINUX_DEFAULT, replacing any existing lsm= token and
 * appending ours. Returns 0 when the file could not be updated. */
static int rewrite_grub_default(const char *lsm)
{
    FILE *in = fopen(GRUB_DEFAULT, "r");
    if (!in)
        return 0;

    char tmp[PATH_BUF_SIZE];
    snprintf(tmp, sizeof(tmp), "%s.ghelper-tmp", GRUB_DEFAULT);
    FILE *out = fopen(tmp, "w");
    if (!out)
    {
        fclose(in);
        return 0;
    }

    char line[2048];
    int patched = 0;
    while (fgets(line, sizeof(line), in))
    {
        if (!patched && strncmp(line, GRUB_KEY "=", strlen(GRUB_KEY) + 1) == 0)
        {
            /* Split into the quoted value and rebuild it without any old lsm=. */
            char *first = strchr(line, '"');
            char *last = first ? strrchr(line, '"') : NULL;
            if (first && last && last > first)
            {
                *last = '\0';
                char rebuilt[2048] = {0};
                size_t used = 0;
                for (char *tok = strtok(first + 1, " "); tok; tok = strtok(NULL, " "))
                {
                    if (strncmp(tok, "lsm=", 4) == 0)
                    {
                        snprintf(replaced_lsm, sizeof(replaced_lsm), "%s", tok);
                        continue; /* dropped, ours goes on the end */
                    }
                    int w = snprintf(rebuilt + used, sizeof(rebuilt) - used,
                                     "%s%s", used ? " " : "", tok);
                    if (w < 0 || (size_t)w >= sizeof(rebuilt) - used)
                        break;
                    used += (size_t)w;
                }
                snprintf(rebuilt + used, sizeof(rebuilt) - used,
                         "%slsm=%s", used ? " " : "", lsm);
                fprintf(out, "%s=\"%s\"\n", GRUB_KEY, rebuilt);
                patched = 1;
                continue;
            }
        }
        fputs(line, out);
    }

    /* No such line in the file: add one. */
    if (!patched)
        fprintf(out, "%s=\"lsm=%s\"\n", GRUB_KEY, lsm);

    fclose(in);
    if (fclose(out) != 0)
    {
        unlink(tmp);
        return 0;
    }

    if (rename(tmp, GRUB_DEFAULT) != 0)
    {
        fprintf(stderr, "lsm-enable: rename %s: %s\n", GRUB_DEFAULT, strerror(errno));
        unlink(tmp);
        return 0;
    }
    return 1;
}

static int regenerate_grub(void)
{
#ifdef GH_LSM_TEST
    return 1;
#endif
    /* update-grub on Debian/Ubuntu, grub-mkconfig elsewhere. grub2- prefix on
     * older Fedora/SUSE layouts. */
    if (have_tool("update-grub"))
    {
        char *const argv[] = {"update-grub", NULL};
        return run_status(argv) == 0;
    }
    if (have_tool("grub-mkconfig") && file_exists("/boot/grub"))
    {
        char *const argv[] = {"grub-mkconfig", "-o", "/boot/grub/grub.cfg", NULL};
        return run_status(argv) == 0;
    }
    if (have_tool("grub2-mkconfig") && file_exists("/boot/grub2"))
    {
        char *const argv[] = {"grub2-mkconfig", "-o", "/boot/grub2/grub.cfg", NULL};
        return run_status(argv) == 0;
    }
    return 0;
}

static int try_grub_default(const char *lsm)
{
    if (!file_exists(GRUB_DEFAULT))
        return 0;
    if (!have_tool("update-grub") && !have_tool("grub-mkconfig") && !have_tool("grub2-mkconfig"))
        return 0;

    if (!copy_backup(GRUB_DEFAULT))
    {
        fprintf(stderr, "lsm-enable: could not back up %s\n", GRUB_DEFAULT);
        return -1;
    }
    if (!rewrite_grub_default(lsm))
    {
        fprintf(stderr, "lsm-enable: could not update %s\n", GRUB_DEFAULT);
        return -1;
    }
    glog(LOG_INFO, "lsm-enable: set lsm=%s in %s", lsm, GRUB_DEFAULT);

    if (!regenerate_grub())
    {
        fprintf(stderr, "lsm-enable: grub config regeneration failed; "
                        "%s was updated, regenerate manually\n",
                GRUB_DEFAULT);
        return -1;
    }
    printf("method=grub\n");
    return 1;
}

/* ---------- systemd-boot (Arch, CachyOS) ---------- */

/* /etc/kernel/cmdline first: kernel updates regenerate the loader entries. */
static int try_systemd_boot(const char *lsm)
{
    if (!have_tool("bootctl"))
        return 0;

    char *const check[] = {"bootctl", "is-installed", "--quiet", NULL};
    if (run_status(check) != 0)
        return 0;

    if (!file_exists(KERNEL_CMDLINE))
        return 0;

    if (!copy_backup(KERNEL_CMDLINE))
        return -1;

    FILE *f = fopen(KERNEL_CMDLINE, "r");
    if (!f)
        return -1;
    char current[2048] = {0};
    if (!fgets(current, sizeof(current), f))
        current[0] = '\0';
    fclose(f);

    char *nl = strchr(current, '\n');
    if (nl)
        *nl = '\0';

    char rebuilt[2048] = {0};
    size_t used = 0;
    for (char *tok = strtok(current, " "); tok; tok = strtok(NULL, " "))
    {
        if (strncmp(tok, "lsm=", 4) == 0)
        {
            snprintf(replaced_lsm, sizeof(replaced_lsm), "%s", tok);
            continue;
        }
        int w = snprintf(rebuilt + used, sizeof(rebuilt) - used, "%s%s", used ? " " : "", tok);
        if (w < 0 || (size_t)w >= sizeof(rebuilt) - used)
            break;
        used += (size_t)w;
    }
    snprintf(rebuilt + used, sizeof(rebuilt) - used, "%slsm=%s", used ? " " : "", lsm);

    f = fopen(KERNEL_CMDLINE, "w");
    if (!f)
        return -1;
    fprintf(f, "%s\n", rebuilt);
    fclose(f);
    glog(LOG_INFO, "lsm-enable: set lsm=%s in /etc/kernel/cmdline", lsm);

    /* Reinstall entries so the new cmdline is picked up. */
    if (have_tool("kernel-install"))
    {
        char *const argv[] = {"kernel-install", "add-all", NULL};
        if (run_status(argv) != 0)
            fprintf(stderr, "lsm-enable: kernel-install add-all failed; "
                            "regenerate boot entries manually\n");
    }
    printf("method=systemd-boot\n");
    return 1;
}

/* ---------- state for lsm-disable ---------- */

static void write_state(const char *method, const char *lsm)
{
    char dir[PATH_BUF_SIZE];
    snprintf(dir, sizeof(dir), "%s", LSM_STATE);
    char *slash = strrchr(dir, '/');
    if (slash)
    {
        *slash = '\0';
        mkdir(dir, 0755);
    }
    FILE *f = fopen(LSM_STATE, "w");
    if (!f)
    {
        glog(LOG_WARNING, "lsm-enable: cannot write %s, lsm-disable will not know the change", LSM_STATE);
        return;
    }
    fprintf(f, "method=%s\nadded=lsm=%s\nreplaced=%s\n", method, lsm, replaced_lsm);
    fclose(f);
}

/* ---------- dispatch ---------- */

int do_lsm_enable(int argc, char **argv)
{
    (void)argc;
    (void)argv;

    char lsm[LSM_BUF];
    if (!build_lsm_list(lsm, sizeof(lsm)))
        return 1;

    /* NixOS is declarative: the caller shows the parameter to add. */
    if (file_exists(NIXOS_MARK))
    {
        fprintf(stderr, "lsm-enable: NixOS is declarative, add the kernel "
                        "parameter to configuration.nix\n");
        printf("method=nixos\nlsm=%s\n", lsm);
        return RC_MANUAL;
    }

    struct
    {
        const char *name;
        int (*fn)(const char *);
    } methods[] = {
        {"rpm-ostree", try_ostree},
        {"grubby", try_grubby},
        {"grub", try_grub_default},
        {"systemd-boot", try_systemd_boot},
        {NULL, NULL},
    };

    for (int i = 0; methods[i].name; i++)
    {
        int rc = methods[i].fn(lsm);
        if (rc == 1)
        {
            write_state(methods[i].name, lsm);
            printf("lsm=%s\n", lsm);
            glog(LOG_INFO, "lsm-enable: applied via %s, reboot required", methods[i].name);
            return RC_REBOOT;
        }
        if (rc < 0)
        {
            /* The method matched this system but failed. Stop rather than
             * letting a less appropriate method touch the bootloader. */
            return 3;
        }
    }

    fprintf(stderr, "lsm-enable: no supported bootloader found\n");
    printf("method=unknown\nlsm=%s\n", lsm);
    return RC_MANUAL;
}

/* ---------- lsm-disable ---------- */

/* Copy value to out with the token `added` swapped for `replaced`, or dropped
 * when replaced is empty. Returns 1 when added was found. */
static int swap_token(const char *value, const char *added, const char *replaced,
                      char *out, size_t out_size)
{
    char work[2048];
    snprintf(work, sizeof(work), "%s", value);
    size_t used = 0;
    int found = 0;
    out[0] = '\0';
    for (char *tok = strtok(work, " \n"); tok; tok = strtok(NULL, " \n"))
    {
        const char *w = tok;
        if (!found && strcmp(tok, added) == 0)
        {
            found = 1;
            if (!*replaced)
                continue;
            w = replaced;
        }
        int n = snprintf(out + used, out_size - used, "%s%s", used ? " " : "", w);
        if (n < 0 || (size_t)n >= out_size - used)
            return 0;
        used += (size_t)n;
    }
    return found;
}

/* The quoted GRUB_CMDLINE_LINUX_DEFAULT value of a grub defaults file. */
static int grub_value(const char *path, char *out, size_t out_size)
{
    FILE *f = fopen(path, "r");
    if (!f)
        return 0;
    char line[2048];
    int ok = 0;
    while (!ok && fgets(line, sizeof(line), f))
    {
        if (strncmp(line, GRUB_KEY "=", strlen(GRUB_KEY) + 1) != 0)
            continue;
        char *first = strchr(line, '"');
        char *last = first ? strrchr(line, '"') : NULL;
        if (first && last && last > first)
        {
            *last = '\0';
            snprintf(out, out_size, "%s", first + 1);
            ok = 1;
        }
    }
    fclose(f);
    return ok;
}

static int revert_grub_default(const char *added, const char *replaced)
{
    char value[2048], rebuilt[2048];
    if (!grub_value(GRUB_DEFAULT, value, sizeof(value)) ||
        !swap_token(value, added, replaced, rebuilt, sizeof(rebuilt)))
        return 0;

    FILE *in = fopen(GRUB_DEFAULT, "r");
    if (!in)
        return -1;
    char tmp[PATH_BUF_SIZE];
    snprintf(tmp, sizeof(tmp), "%s.ghelper-tmp", GRUB_DEFAULT);
    FILE *out = fopen(tmp, "w");
    if (!out)
    {
        fclose(in);
        return -1;
    }
    char line[2048];
    int patched = 0;
    while (fgets(line, sizeof(line), in))
    {
        if (!patched && strncmp(line, GRUB_KEY "=", strlen(GRUB_KEY) + 1) == 0)
        {
            fprintf(out, "%s=\"%s\"\n", GRUB_KEY, rebuilt);
            patched = 1;
            continue;
        }
        fputs(line, out);
    }
    fclose(in);
    if (fclose(out) != 0 || rename(tmp, GRUB_DEFAULT) != 0)
    {
        unlink(tmp);
        return -1;
    }
    return regenerate_grub() ? 1 : -1;
}

static int revert_systemd_boot(const char *added, const char *replaced)
{
    char value[2048] = {0}, rebuilt[2048];
    FILE *f = fopen(KERNEL_CMDLINE, "r");
    if (!f)
        return 0;
    if (!fgets(value, sizeof(value), f))
        value[0] = '\0';
    fclose(f);
    if (!swap_token(value, added, replaced, rebuilt, sizeof(rebuilt)))
        return 0;

    f = fopen(KERNEL_CMDLINE, "w");
    if (!f)
        return -1;
    fprintf(f, "%s\n", rebuilt);
    fclose(f);
    if (have_tool("kernel-install"))
    {
        char *const argv[] = {"kernel-install", "add-all", NULL};
        if (run_status(argv) != 0)
            fprintf(stderr, "lsm-disable: kernel-install add-all failed; "
                            "regenerate boot entries manually\n");
    }
    return 1;
}

static int revert_ostree(const char *added)
{
    if (!have_tool("rpm-ostree"))
        return -1;
    char arg[LSM_BUF + 32];
    snprintf(arg, sizeof(arg), "--delete-if-present=%s", added);
    char *const argv[] = {"rpm-ostree", "kargs", arg, NULL};
    return run_status(argv) == 0 ? 1 : -1;
}

static int revert_grubby(const char *replaced)
{
    if (!have_tool("grubby"))
        return -1;
    char *const rm[] = {"grubby", "--update-kernel=ALL", "--remove-args=lsm", NULL};
    if (run_status(rm) != 0)
        return -1;
    if (!*replaced)
        return 1;
    char arg[LSM_BUF + 16];
    snprintf(arg, sizeof(arg), "--args=%s", replaced);
    char *const add[] = {"grubby", "--update-kernel=ALL", arg, NULL};
    return run_status(add) == 0 ? 1 : -1;
}

/* Undo lsm-enable. Exit 0 = nothing of ours to undo, 5 = reverted (reboot),
 * 6 = the token was changed since, so it is left alone, 3 = failed. */
int do_lsm_disable(int argc, char **argv)
{
    (void)argc;
    (void)argv;

    char method[32] = {0}, added[LSM_BUF + 32] = {0}, replaced[LSM_BUF + 32] = {0};
    FILE *f = fopen(LSM_STATE, "r");
    if (f)
    {
        char line[LSM_BUF + 32];
        while (fgets(line, sizeof(line), f))
        {
            line[strcspn(line, "\n")] = '\0';
            if (strncmp(line, "method=", 7) == 0)
                snprintf(method, sizeof(method), "%.31s", line + 7);
            else if (strncmp(line, "added=", 6) == 0)
                snprintf(added, sizeof(added), "%s", line + 6);
            else if (strncmp(line, "replaced=", 9) == 0)
                snprintf(replaced, sizeof(replaced), "%s", line + 9);
        }
        fclose(f);
    }

    if (!method[0] || strncmp(added, "lsm=", 4) != 0 || strcmp(added, replaced) == 0)
    {
        printf("lsm=none\n");
        return 0;
    }

    int rc;
    if (strcmp(method, "grub") == 0)
        rc = revert_grub_default(added, replaced);
    else if (strcmp(method, "systemd-boot") == 0)
        rc = revert_systemd_boot(added, replaced);
    else if (strcmp(method, "rpm-ostree") == 0)
        rc = revert_ostree(added);
    else if (strcmp(method, "grubby") == 0)
        rc = revert_grubby(replaced);
    else
        rc = -1;

    if (rc == 0)
    {
        fprintf(stderr, "lsm-disable: %s is no longer in the boot config, leaving it\n", added);
        printf("lsm=changed\n");
        unlink(LSM_STATE);
        return RC_MANUAL;
    }
    if (rc < 0)
    {
        fprintf(stderr, "lsm-disable: could not revert %s via %s\n", added, method);
        return 3;
    }

    char bak[PATH_BUF_SIZE];
    snprintf(bak, sizeof(bak), "%s.ghelper-bak",
             strcmp(method, "systemd-boot") == 0 ? KERNEL_CMDLINE : GRUB_DEFAULT);
    if (strcmp(method, "grub") == 0 || strcmp(method, "systemd-boot") == 0)
        unlink(bak);
    unlink(LSM_STATE);
    glog(LOG_INFO, "lsm-disable: removed %s via %s, reboot required", added, method);
    printf("method=%s\nlsm=reverted\n", method);
    return RC_REBOOT;
}
