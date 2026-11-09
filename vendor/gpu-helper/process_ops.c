/* Process detection and service-aware kill for gpu-helper. */
#include "gpu-helper.h"

/* ---------- shared /proc detection ---------- */

/* One pass over /proc/<pid>/maps. devMapped: a /dev/nvidia* mapping (pins the
 * driver exactly like an open fd, even after the fd is closed - rmmod fails
 * while it exists). libsMapped: NVIDIA userspace libs loaded. */
static void scan_maps(int pid, int *libsMapped, int *devMapped)
{
    *libsMapped = 0;
    *devMapped = 0;
    char path[64];
    snprintf(path, sizeof(path), "/proc/%d/maps", pid);
    FILE *f = fopen(path, "r");
    if (!f)
        return;
    char line[MAPS_LINE_SIZE];
    while (fgets(line, sizeof(line), f) != NULL)
    {
        if (!*devMapped && strstr(line, NVIDIA_PREFIX) != NULL)
            *devMapped = 1;
        if (!*libsMapped && (strstr(line, "/libnvidia-") != NULL || strstr(line, "/libcuda.so") != NULL || strstr(line, "/libnvcuvid.so") != NULL))
            *libsMapped = 1;
        if (*devMapped && *libsMapped)
            break;
    }
    fclose(f);
}

/* ---------- nvidia DRM + I2C device discovery ---------- */

#define MAX_AUX_DEVICES 32

static int nvidia_card_nums[MAX_AUX_DEVICES];
static int nvidia_card_count = 0;
static int nvidia_render_nums[MAX_AUX_DEVICES];
static int nvidia_render_count = 0;
static int nvidia_i2c_nums[MAX_AUX_DEVICES];
static int nvidia_i2c_count = 0;
static int aux_devices_resolved = 0;

/* The nvidia driver parents its I2C adapters directly on the GPU's PCI device
 * (nv-i2c.c: dev.parent = nvl->dev), so /sys/bus/i2c/devices/i2c-N resolves to
 * .../0000:01:00.0/i2c-N. Returns 1 when the parent's PCI vendor is 0x10de,
 * 0 when it is some other vendor, -1 when indeterminate (no vendor attribute,
 * e.g. a non-PCI/SOC parent) - callers fall back to name matching then. */
static int i2c_parent_is_nvidia_pci(const char *i2cname)
{
    char link[PATH_BUF_SIZE];
    char real[PATH_MAX];
    snprintf(link, sizeof(link), "/sys/bus/i2c/devices/%s", i2cname);
    if (realpath(link, real) == NULL)
        return -1;
    char *slash = strrchr(real, '/');
    if (slash == NULL)
        return -1;
    *slash = '\0'; /* parent dir = owning device */
    char vendor_path[PATH_MAX + 8];
    snprintf(vendor_path, sizeof(vendor_path), "%s/vendor", real);
    FILE *f = fopen(vendor_path, "r");
    if (!f)
        return -1;
    char buf[32];
    int nv = (fgets(buf, sizeof(buf), f) && strncmp(buf, "0x10de", 6) == 0);
    fclose(f);
    return nv;
}

/* Discover DRM card/renderD and I2C adapters owned by nvidia, so the holder
 * scan can detect processes holding DRI or I2C devices that pin the nvidia
 * module refcount (invisible to bare /dev/nvidia scans). */
static void resolve_aux_devices(void)
{
    if (aux_devices_resolved)
        return;
    aux_devices_resolved = 1;

    DIR *pcidir = opendir("/sys/bus/pci/devices");
    if (pcidir)
    {
        struct dirent *e;
        char path[PATH_BUF_SIZE];
        char buf[32];
        while ((e = readdir(pcidir)) != NULL)
        {
            if (e->d_name[0] == '.')
                continue;
            snprintf(path, sizeof(path), "/sys/bus/pci/devices/%s/vendor", e->d_name);
            FILE *f = fopen(path, "r");
            if (!f)
                continue;
            int nv = (fgets(buf, sizeof(buf), f) && strncmp(buf, "0x10de", 6) == 0);
            fclose(f);
            if (!nv)
                continue;

            snprintf(path, sizeof(path), "/sys/bus/pci/devices/%s/drm", e->d_name);
            DIR *drmdir = opendir(path);
            if (!drmdir)
                continue;
            struct dirent *de;
            while ((de = readdir(drmdir)) != NULL)
            {
                if (strncmp(de->d_name, "card", 4) == 0 && isdigit((unsigned char)de->d_name[4]) && !strchr(de->d_name + 4, '-') && nvidia_card_count < MAX_AUX_DEVICES)
                    nvidia_card_nums[nvidia_card_count++] = atoi(de->d_name + 4);
                else if (strncmp(de->d_name, "renderD", 7) == 0 && isdigit((unsigned char)de->d_name[7]) && nvidia_render_count < MAX_AUX_DEVICES)
                    nvidia_render_nums[nvidia_render_count++] = atoi(de->d_name + 7);
            }
            closedir(drmdir);
        }
        closedir(pcidir);
    }

    /* I2C adapters: nvidia registers them with name "NVIDIA i2c adapter ..."
     * and parent = the PCI device. Programs like powerdevil or OpenRGB hold
     * /dev/i2c-N for DDC/CI, silently pinning the nvidia module refcount. */
    DIR *i2cdir = opendir("/sys/bus/i2c/devices");
    if (i2cdir)
    {
        struct dirent *e;
        char path[PATH_BUF_SIZE];
        char name[256];
        while ((e = readdir(i2cdir)) != NULL)
        {
            if (strncmp(e->d_name, "i2c-", 4) != 0)
                continue;
            snprintf(path, sizeof(path), "/sys/bus/i2c/devices/%s/name", e->d_name);
            FILE *f = fopen(path, "r");
            if (!f)
                continue;
            int found = (fgets(name, sizeof(name), f) && strstr(name, "NVIDIA") != NULL);
            fclose(f);
            /* Adapter names could in principle collide; when the adapter's
             * sysfs parent exposes a PCI vendor, require 0x10de. -1 (no PCI
             * parent, e.g. SOC) falls back to the name match. */
            if (found && i2c_parent_is_nvidia_pci(e->d_name) == 0)
                found = 0;
            if (found && nvidia_i2c_count < MAX_AUX_DEVICES)
                nvidia_i2c_nums[nvidia_i2c_count++] = atoi(e->d_name + 4);
        }
        closedir(i2cdir);
    }
}

/* Single pass over /proc/<pid>/fd counting nvidia, DRI, and I2C fds. */

struct gpu_fd_counts
{
    int nvidia;
    int dri;
    int i2c;
};

static struct gpu_fd_counts count_all_gpu_fds(int pid)
{
    struct gpu_fd_counts c = {0, 0, 0};
    char fdDir[64];
    snprintf(fdDir, sizeof(fdDir), "/proc/%d/fd", pid);
    DIR *dir = opendir(fdDir);
    if (!dir)
        return c;
    struct dirent *e;
    char fdPath[PATH_BUF_SIZE];
    char target[PATH_BUF_SIZE];
    while ((e = readdir(dir)) != NULL)
    {
        if (e->d_name[0] == '.')
            continue;
        snprintf(fdPath, sizeof(fdPath), "%s/%s", fdDir, e->d_name);
        ssize_t n = readlink(fdPath, target, sizeof(target) - 1);
        if (n <= 0)
            continue;
        target[n] = '\0';

        if (strncmp(target, NVIDIA_PREFIX, NVIDIA_PREFIX_LEN) == 0)
        {
            c.nvidia++;
        }
        else if (strncmp(target, "/dev/dri/card", 13) == 0)
        {
            int num = atoi(target + 13);
            for (int i = 0; i < nvidia_card_count; i++)
                if (nvidia_card_nums[i] == num)
                {
                    c.dri++;
                    break;
                }
        }
        else if (strncmp(target, "/dev/dri/renderD", 16) == 0)
        {
            int num = atoi(target + 16);
            for (int i = 0; i < nvidia_render_count; i++)
                if (nvidia_render_nums[i] == num)
                {
                    c.dri++;
                    break;
                }
        }
        else if (strncmp(target, "/dev/i2c-", 9) == 0)
        {
            int num = atoi(target + 9);
            for (int i = 0; i < nvidia_i2c_count; i++)
                if (nvidia_i2c_nums[i] == num)
                {
                    c.i2c++;
                    break;
                }
        }
    }
    closedir(dir);
    return c;
}

static int is_holder(int pid)
{
    /* The kill path reaches here without do_list's resolve; without it the
     * DRI/I2C tables are empty and an i2c-only holder (powerdevil) would not
     * be recognized, so its service would never be stopped before the kill. */
    resolve_aux_devices();
    struct gpu_fd_counts c = count_all_gpu_fds(pid);
    if (c.nvidia > 0 || c.dri > 0 || c.i2c > 0)
        return 1;
    int libs, dev;
    scan_maps(pid, &libs, &dev);
    return libs || dev;
}

static unsigned int read_uid(int pid)
{
    char path[64];
    snprintf(path, sizeof(path), "/proc/%d/status", pid);
    FILE *f = fopen(path, "r");
    if (!f)
        return (unsigned int)-1;
    char line[256];
    unsigned int uid = (unsigned int)-1;
    while (fgets(line, sizeof(line), f) != NULL)
    {
        if (strncmp(line, "Uid:", 4) == 0)
        {
            sscanf(line + 4, " %u", &uid);
            break;
        }
    }
    fclose(f);
    return uid;
}

static void read_comm(int pid, char *out, size_t outsz)
{
    char path[64];
    snprintf(path, sizeof(path), "/proc/%d/comm", pid);
    FILE *f = fopen(path, "r");
    if (!f)
    {
        strncpy(out, "?", outsz - 1);
        out[outsz - 1] = '\0';
        return;
    }
    if (fgets(out, outsz, f) != NULL)
    {
        size_t n = strlen(out);
        if (n > 0 && out[n - 1] == '\n')
            out[n - 1] = '\0';
        for (size_t i = 0; i < n; i++)
            if (out[i] == '\t' || out[i] == '\n')
                out[i] = ' ';
    }
    else
    {
        strncpy(out, "?", outsz - 1);
        out[outsz - 1] = '\0';
    }
    fclose(f);
}

/* Read /proc/<pid>/cgroup (cgroup v2: single "0::<path>" line) into out. */
static int read_cgroup(int pid, char *out, size_t outsz)
{
    char path[64];
    snprintf(path, sizeof(path), "/proc/%d/cgroup", pid);
    FILE *f = fopen(path, "r");
    if (!f)
        return 0;
    out[0] = '\0';
    char line[CGROUP_BUF_SIZE];
    while (fgets(line, sizeof(line), f) != NULL)
    {
        if (strncmp(line, "0::", 3) == 0)
        {
            strncpy(out, line + 3, outsz - 1);
            out[outsz - 1] = '\0';
            break;
        }
        if (out[0] == '\0')
        {
            const char *p = strrchr(line, ':');
            if (p)
            {
                strncpy(out, p + 1, outsz - 1);
                out[outsz - 1] = '\0';
            }
        }
    }
    fclose(f);
    size_t n = strlen(out);
    if (n > 0 && out[n - 1] == '\n')
        out[n - 1] = '\0';
    return out[0] != '\0';
}

/* 0 none, 1 system service (unit), 2 user service (unit + *uid) */
static int classify_service(const char *cgroup, char *unit, size_t unitsz, unsigned int *uid)
{
    const char *leaf = strrchr(cgroup, '/');
    leaf = leaf ? leaf + 1 : cgroup;
    size_t len = strlen(leaf);
    const char *svc = ".service";
    size_t svclen = strlen(svc);
    if (len <= svclen || strcmp(leaf + len - svclen, svc) != 0)
        return 0;
    strncpy(unit, leaf, unitsz - 1);
    unit[unitsz - 1] = '\0';
    const char *u = strstr(cgroup, "user@");
    if (u != NULL)
    {
        unsigned int parsed = (unsigned int)-1;
        if (sscanf(u + 5, "%u", &parsed) == 1)
        {
            *uid = parsed;
            return 2;
        }
    }
    return 1;
}

/* ---------- kill (service-aware) ---------- */

static char g_stopped[MAX_STOPPED_UNITS][UNIT_BUF_SIZE];
static int g_stopped_count = 0;

static int already_stopped(const char *unit)
{
    for (int i = 0; i < g_stopped_count; i++)
        if (strcmp(g_stopped[i], unit) == 0)
            return 1;
    return 0;
}
static void remember_stopped(const char *unit)
{
    if (g_stopped_count < MAX_STOPPED_UNITS)
    {
        snprintf(g_stopped[g_stopped_count], UNIT_BUF_SIZE, "%s", unit);
        g_stopped_count++;
    }
}

static void stop_service(int pid)
{
    if (!is_holder(pid))
        return;
    char cgroup[CGROUP_BUF_SIZE];
    if (!read_cgroup(pid, cgroup, sizeof(cgroup)))
        return;
    char unit[UNIT_BUF_SIZE];
    unsigned int uid = (unsigned int)-1;
    int kind = classify_service(cgroup, unit, sizeof(unit), &uid);
    if (kind == 0)
        return;
    if (already_stopped(unit))
        return;
    remember_stopped(unit);
    if (kind == 1)
    {
        printf("stopped %s (system service, pid %d)\n", unit, pid);
        glog(LOG_INFO, "kill: stop system service %s (pid %d)", unit, pid);
        char *const argv[] = {"timeout", "10", "systemctl", "stop", unit, NULL};
        run_cmd(argv);
    }
    else
    {
        struct passwd *pw = getpwuid((uid_t)uid);
        if (pw == NULL || pw->pw_name == NULL)
            return;
        printf("stopped %s (user service uid %u, pid %d)\n", unit, uid, pid);
        glog(LOG_INFO, "kill: stop user service %s (uid %u, pid %d)", unit, uid, pid);
        char xdg[64];
        snprintf(xdg, sizeof(xdg), "XDG_RUNTIME_DIR=/run/user/%u", uid);
        char *const argv[] = {
            "timeout", "10", "runuser", "-u", pw->pw_name, "--",
            "env", xdg, "systemctl", "--user", "stop", unit, NULL};
        run_cmd(argv);
    }
}

/* Pids in units whose stop ends the boot or the session; never signalled. */
static int in_protected_unit(int pid)
{
    static const char *const keep[] = {"init.scope", "systemd-", "dbus", "display-manager", "sddm", "gdm", "lightdm"};
    char cgroup[CGROUP_BUF_SIZE];
    if (!read_cgroup(pid, cgroup, sizeof(cgroup)))
        return 0;
    const char *leaf = strrchr(cgroup, '/');
    leaf = leaf ? leaf + 1 : cgroup;
    for (size_t i = 0; i < sizeof(keep) / sizeof(keep[0]); i++)
        if (strncmp(leaf, keep[i], strlen(keep[i])) == 0)
            return 1;
    return 0;
}

static int parse_signal(const char *s)
{
    if (strcmp(s, "-15") == 0 || strcmp(s, "15") == 0 || strcmp(s, "TERM") == 0 || strcmp(s, "SIGTERM") == 0)
        return SIGTERM;
    if (strcmp(s, "-9") == 0 || strcmp(s, "9") == 0 || strcmp(s, "KILL") == 0 || strcmp(s, "SIGKILL") == 0)
        return SIGKILL;
    return -1;
}

int do_kill(int argc, char **argv)
{
    if (argc < 4)
    {
        fprintf(stderr, "usage: kill <signal> <pid> [pid...]\n");
        return 1;
    }
    int sig = parse_signal(argv[2]);
    if (sig < 0)
    {
        fprintf(stderr, "kill: only -15 and -9 are allowed\n");
        return 2;
    }
    int signalled = 0;
    for (int i = 3; i < argc; i++)
    {
        int pid = atoi(argv[i]);
        if (pid <= 1)
            continue;
        if (pid == (int)getpid())
            continue;
        if (in_protected_unit(pid))
            continue;
        stop_service(pid);
        if (kill(pid, sig) == 0)
            signalled++;
    }
    glog(LOG_INFO, "kill: signal %d sent to %d/%d pid(s), %d service(s) stopped",
         sig, signalled, argc - 3, g_stopped_count);
    return 0;
}

/* ---------- list ---------- */

int do_list(int skip_pid)
{
    resolve_aux_devices();

    DIR *proc = opendir("/proc");
    if (!proc)
    {
        fprintf(stderr, "gpu-helper: cannot open /proc\n");
        return 1;
    }
    struct dirent *e;
    char comm[COMM_BUF_SIZE];
    while ((e = readdir(proc)) != NULL)
    {
        if (!isdigit((unsigned char)e->d_name[0]))
            continue;
        int pid = atoi(e->d_name);
        if (pid <= 0 || pid == skip_pid)
            continue;
        struct gpu_fd_counts c = count_all_gpu_fds(pid);
        int libs, devMapped;
        scan_maps(pid, &libs, &devMapped);
        int fds = c.nvidia;
        /* A closed-fd /dev/nvidia* mapping still pins the driver - report it
         * in the fd column so callers treat it as an rmmod blocker. */
        if (fds == 0 && devMapped)
            fds = 1;
        if (fds > 0 || libs || c.dri > 0 || c.i2c > 0)
        {
            unsigned int uid = read_uid(pid);
            read_comm(pid, comm, sizeof(comm));
            const char *unit_out = "-";
            char cgroup[CGROUP_BUF_SIZE];
            char unit[UNIT_BUF_SIZE];
            unsigned int suid;
            if (read_cgroup(pid, cgroup, sizeof(cgroup)) && classify_service(cgroup, unit, sizeof(unit), &suid) != 0)
                unit_out = unit;
            printf("%d\t%d\t%u\t%s\t%d\t%s\t%d\t%d\n", pid, fds, uid, comm, libs, unit_out, c.dri, c.i2c);
        }
    }
    closedir(proc);
    return 0;
}
