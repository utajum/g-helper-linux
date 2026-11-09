/* eBPF GPU block: capability probe, load/attach (pinned in bpffs), blocked-file bookkeeping. */

#include "gpu-helper.h"
#include <stdbool.h>
#include <sys/mount.h>
#include <sys/stat.h>
#include <sys/sysmacros.h>
#include <sys/utsname.h>
#include <sys/vfs.h>
#include <time.h>

/* Generated from the compiled filter by build.sh. Absent when the build had no
 * clang, in which case the feature reports itself unavailable. */
#if defined(GH_HAVE_BPF_BLOB)
#include "bpf_blob.h"
#else
static const unsigned char gh_bpf_obj[] = {0};
static const unsigned int gh_bpf_obj_len = 0;
#endif

#define BPF_FS_DIR "/sys/fs/bpf"
#define PIN_DIR "/sys/fs/bpf/ghelper"
#define PIN_MODE PIN_DIR "/gh_mode"
#define PIN_BLOCKED PIN_DIR "/gh_blocked_ino"
#define PIN_COMM PIN_DIR "/gh_allowed_comm"
#define PIN_DIRENT PIN_DIR "/gh_hidden_dirent"
#define PIN_PID PIN_DIR "/gh_allowed_pid"
#define PIN_ENV PIN_DIR "/gh_allowed_env"
#define PIN_REFUSED PIN_DIR "/gh_refused"
#define LSM_PATH "/sys/kernel/security/lsm"
#define BPF_FS_MAGIC 0xcafe4a11

/* Must match struct gh_file_key in the filter. */
struct gh_file_key
{
    uint32_t dev;
    uint32_t pad;
    uint64_t ino;
};

/* Built-in names that keep GPU access; denying them breaks the system or the power monitor. */
static const char *const ALLOWED_COMMS[] = {
    "ghelper", "gpu-helper", "(udev-worker)", "systemd-udevd",
    "pacman", "dnf", "apt", "apt-get", "dpkg", "rpm",
    "nix", "nix-daemon", "rpm-ostree", "zypper", "switcheroo-cont", NULL};

/* Environment entries that allow the dGPU; the forced ones also hide the iGPU. */
static const char *const ALLOWED_ENVS[] = {
    "__NV_PRIME_RENDER_OFFLOAD=1", "DRI_PRIME=1", "GHELPER_DGPU=1", "CARDWIRE_ALLOW=1", NULL};
static const char *const FORCED_ENVS[] = {"GHELPER_FORCE_DGPU=1", "CARDWIRE_FORCE_DGPU=1", NULL};

/* User allowed apps, read at attach time: the boot service attaches before the desktop starts. */
#define USER_COMMS_FILE "/etc/ghelper/gpu-allowed-comms"
/* The map holds 64 keys and the table above takes some, so cap what a user
 * can add and keep room for the built-ins. */
#define USER_COMMS_MAX 50
/* TASK_COMM_LEN: 15 usable characters plus the terminator. */
#define GH_COMM_LEN 16
/* Map value for user picks; must match GH_COMM_USER in the filter. */
#define GH_COMM_USER 2
/* Must match the filter: environment key size, inode kinds, pid levels. */
#define GH_ENV_LEN 48
#define GH_INO_DGPU 1
#define GH_INO_IGPU 2
#define GH_PID_ALLOW 1
#define GH_PID_FORCE 2
/* User environment entries ("SteamAppId=570"); the map holds 128 keys. */
#define USER_ENVS_MAX 64

/* A comm the kernel could actually report. Rejects separators and anything
 * that would let a crafted line write outside its own key. */
static int valid_comm(const char *s)
{
    size_t n = strlen(s);
    if (n == 0 || n >= GH_COMM_LEN)
        return 0;
    for (size_t i = 0; i < n; i++)
    {
        unsigned char c = (unsigned char)s[i];
        if (c <= 0x20 || c == 0x7f || c == '/' || c == '\\' || c == '=')
            return 0;
    }
    return 1;
}

/* KEY=VALUE from a small charset; the '=' is what tells it from a comm. */
static int valid_env(const char *s)
{
    size_t n = strlen(s);
    const char *eq = strchr(s, '=');
    if (n >= GH_ENV_LEN || !eq || eq == s || !eq[1])
        return 0;
    for (size_t i = 0; i < n; i++)
        if (!isalnum((unsigned char)s[i]) && !strchr("_=.:-", s[i]))
            return 0;
    return 1;
}

/* True for a built-in name; the user cannot remove those. */
static int is_builtin_comm(const char *s)
{
    for (int i = 0; ALLOWED_COMMS[i]; i++)
        if (strcmp(ALLOWED_COMMS[i], s) == 0)
            return 1;
    return 0;
}

/* Read valid user lines into out (stride bytes each); skip invalid, duplicate and built-in ones. */
static int read_user_lines(char *out, size_t stride, int max, int (*valid)(const char *))
{
    FILE *f = fopen(USER_COMMS_FILE, "r");
    if (!f)
        return 0;

    int n = 0;
    char line[128];
    while (n < max && fgets(line, sizeof(line), f))
    {
        char *p = line;
        while (*p == ' ' || *p == '\t')
            p++;
        if (*p == '#' || *p == '\n' || *p == '\r' || *p == '\0')
            continue;
        size_t len = strcspn(p, "\r\n");
        p[len] = '\0';
        if (!valid(p) || (valid == valid_comm && is_builtin_comm(p)))
            continue;

        int dup = 0;
        for (int i = 0; i < n; i++)
            if (strcmp(out + i * stride, p) == 0)
                dup = 1;
        if (dup)
            continue;

        memset(out + n * stride, 0, stride);
        /* the validators cap len below stride, so this never truncates */
        memcpy(out + n * stride, p, strlen(p));
        n++;
    }
    fclose(f);
    return n;
}

static int read_user_comms(char out[USER_COMMS_MAX][GH_COMM_LEN])
{
    return read_user_lines(&out[0][0], GH_COMM_LEN, USER_COMMS_MAX, valid_comm);
}

static int read_user_envs(char out[USER_ENVS_MAX][GH_ENV_LEN])
{
    return read_user_lines(&out[0][0], GH_ENV_LEN, USER_ENVS_MAX, valid_env);
}

/* Rewrite the user list from the in-memory arrays. */
static int write_user_lists(char list[USER_COMMS_MAX][GH_COMM_LEN], int n,
                            char envs[USER_ENVS_MAX][GH_ENV_LEN], int ne)
{
    if (mkdir("/etc/ghelper", 0755) != 0 && errno != EEXIST)
    {
        fprintf(stderr, "bpf-block: cannot create /etc/ghelper: %s\n", strerror(errno));
        return 0;
    }
    char tmp[] = USER_COMMS_FILE ".tmp";
    FILE *f = fopen(tmp, "w");
    if (!f)
    {
        fprintf(stderr, "bpf-block: cannot write %s: %s\n", tmp, strerror(errno));
        return 0;
    }
    fprintf(f, "# Apps allowed to use the dGPU while the block is on.\n");
    fprintf(f, "# One process name or KEY=VALUE environment entry per line, managed by ghelper.\n");
    for (int i = 0; i < n; i++)
        fprintf(f, "%s\n", list[i]);
    for (int i = 0; i < ne; i++)
        fprintf(f, "%s\n", envs[i]);
    fclose(f);
    if (chmod(tmp, 0644) != 0)
        glog(LOG_WARNING, "bpf-block: chmod on the comm list failed");
    if (rename(tmp, USER_COMMS_FILE) != 0)
    {
        fprintf(stderr, "bpf-block: cannot replace %s: %s\n", USER_COMMS_FILE, strerror(errno));
        unlink(tmp);
        return 0;
    }
    return 1;
}

/* ---------- libbpf binding ---------- */

struct bpf_object;
struct bpf_program;
struct bpf_map;
struct bpf_link;

static void *libbpf_handle;
static struct bpf_object *(*p_object_open_mem)(const void *, size_t, const void *);
static int (*p_object_load)(struct bpf_object *);
static void (*p_object_close)(struct bpf_object *);
static struct bpf_program *(*p_find_prog)(const struct bpf_object *, const char *);
static struct bpf_map *(*p_find_map)(const struct bpf_object *, const char *);
static struct bpf_link *(*p_attach_lsm)(const struct bpf_program *);
static struct bpf_link *(*p_attach_tracepoint)(const struct bpf_program *, const char *, const char *);
static struct bpf_link *(*p_attach_raw_tp)(const struct bpf_program *, const char *);
static int (*p_set_autoload)(struct bpf_program *, bool);
static int (*p_link_pin)(struct bpf_link *, const char *);
static int (*p_map_fd)(const struct bpf_map *);
static int (*p_map_pin)(struct bpf_map *, const char *);
static int (*p_map_update)(int, const void *, const void *, uint64_t);
static int (*p_map_lookup)(int, const void *, void *);
static int (*p_map_delete)(int, const void *);
static int (*p_map_next_key)(int, const void *, void *);
static int (*p_obj_get)(const char *);
static void (*p_set_print)(void *);

/* Silence libbpf's own chatter; failures are reported by the caller with
 * context the user can act on. */
static int bpf_quiet_print(int level, const char *fmt, va_list ap)
{
    (void)level;
    (void)fmt;
    (void)ap;
    return 0;
}

static int bind_sym(void **slot, const char *name)
{
    *slot = dlsym(libbpf_handle, name);
    if (!*slot)
    {
        fprintf(stderr, "bpf: libbpf missing %s\n", name);
        return 0;
    }
    return 1;
}

/* dlopen libbpf and resolve everything we call. Returns 0 when unavailable. */
static int load_libbpf(void)
{
    if (libbpf_handle)
        return 1;

    libbpf_handle = dlopen("libbpf.so.1", RTLD_LAZY);
    if (!libbpf_handle)
        libbpf_handle = dlopen("libbpf.so", RTLD_LAZY);
    if (!libbpf_handle)
        return 0;

    if (!bind_sym((void **)&p_object_open_mem, "bpf_object__open_mem") ||
        !bind_sym((void **)&p_object_load, "bpf_object__load") ||
        !bind_sym((void **)&p_object_close, "bpf_object__close") ||
        !bind_sym((void **)&p_find_prog, "bpf_object__find_program_by_name") ||
        !bind_sym((void **)&p_find_map, "bpf_object__find_map_by_name") ||
        !bind_sym((void **)&p_attach_lsm, "bpf_program__attach_lsm") ||
        !bind_sym((void **)&p_link_pin, "bpf_link__pin") ||
        !bind_sym((void **)&p_map_fd, "bpf_map__fd") ||
        !bind_sym((void **)&p_map_pin, "bpf_map__pin") ||
        !bind_sym((void **)&p_map_update, "bpf_map_update_elem") ||
        !bind_sym((void **)&p_map_lookup, "bpf_map_lookup_elem") ||
        !bind_sym((void **)&p_map_delete, "bpf_map_delete_elem") ||
        !bind_sym((void **)&p_map_next_key, "bpf_map_get_next_key") ||
        !bind_sym((void **)&p_obj_get, "bpf_obj_get"))
        return 0;

    /* Optional: only used to keep libbpf quiet. */
    p_set_print = dlsym(libbpf_handle, "libbpf_set_print");
    if (p_set_print)
        p_set_print((void *)bpf_quiet_print);

    /* Optional: without it the directory listing filter is skipped, which
     * degrades the block rather than breaking it. */
    p_attach_tracepoint = dlsym(libbpf_handle, "bpf_program__attach_tracepoint");
    /* Optional: per-process hooks, and the retry without the listing programs. */
    p_attach_raw_tp = dlsym(libbpf_handle, "bpf_program__attach_raw_tracepoint");
    p_set_autoload = dlsym(libbpf_handle, "bpf_program__set_autoload");

    return 1;
}

/* ---------- capability probe ---------- */

/* bpf must be in the active LSM list, which is fixed at boot. Everything else
 * can be fixed at runtime. */
static int lsm_bpf_active(void)
{
    char buf[512];
    int fd = open(LSM_PATH, O_RDONLY);
    if (fd < 0)
        return 0;
    ssize_t n = read(fd, buf, sizeof(buf) - 1);
    close(fd);
    if (n <= 0)
        return 0;
    buf[n] = '\0';

    /* Comma separated, exact token match so "bpf" does not match a substring. */
    for (char *tok = strtok(buf, ",\n"); tok; tok = strtok(NULL, ",\n"))
        if (strcmp(tok, "bpf") == 0)
            return 1;
    return 0;
}

static int bpffs_mounted(void)
{
    struct statfs sfs;
    if (statfs(BPF_FS_DIR, &sfs) != 0)
        return 0;
    return sfs.f_type == (typeof(sfs.f_type))BPF_FS_MAGIC;
}

static int kernel_has_bpf_lsm_config(void)
{
    /* Without CONFIG_BPF_LSM=y the feature cannot be enabled at all. */
    struct utsname un;
    char path[PATH_BUF_SIZE];
    if (uname(&un) != 0)
        return -1;

    snprintf(path, sizeof(path), "/boot/config-%s", un.release);
    FILE *f = fopen(path, "r");
    if (!f)
        f = fopen("/proc/config.gz", "r"); /* gz: presence only, cannot parse */
    if (!f)
        return -1; /* unknown */

    char line[256];
    int found = 0;
    while (fgets(line, sizeof(line), f))
    {
        if (strncmp(line, "CONFIG_BPF_LSM=y", 16) == 0)
        {
            found = 1;
            break;
        }
    }
    fclose(f);
    return found;
}

static const char *lockdown_state(void)
{
    static char buf[128];
    int fd = open("/sys/kernel/security/lockdown", O_RDONLY);
    if (fd < 0)
        return "none";
    ssize_t n = read(fd, buf, sizeof(buf) - 1);
    close(fd);
    if (n <= 0)
        return "none";
    buf[n] = '\0';
    /* Format is "[none] integrity confidentiality" with the active one in
     * brackets. */
    char *open_br = strchr(buf, '[');
    if (!open_br)
        return "none";
    char *close_br = strchr(open_br, ']');
    if (!close_br)
        return "none";
    *close_br = '\0';
    return open_br + 1;
}

int do_bpf_caps(void)
{
    int cfg = kernel_has_bpf_lsm_config();
    int have_lib = load_libbpf();

    printf("lsm_active=%d\n", lsm_bpf_active());
    printf("kernel_config=%s\n", cfg < 0 ? "unknown" : (cfg ? "y" : "n"));
    printf("bpffs=%d\n", bpffs_mounted());
    printf("libbpf=%d\n", have_lib);
    printf("filter=%d\n", gh_bpf_obj_len > 0 ? 1 : 0);
    printf("btf=%d\n", access("/sys/kernel/btf/vmlinux", R_OK) == 0 ? 1 : 0);
    printf("lockdown=%s\n", lockdown_state());
    printf("attached=%d\n", access(PIN_MODE, F_OK) == 0 ? 1 : 0);
    return 0;
}

/* ---------- blocked file set ---------- */

static int read_line(const char *path, char *buf, size_t len)
{
    int fd = open(path, O_RDONLY);
    if (fd < 0)
        return 0;
    ssize_t n = read(fd, buf, len - 1);
    close(fd);
    if (n <= 0)
        return 0;
    buf[n] = '\0';
    char *nl = strchr(buf, '\n');
    if (nl)
        *nl = '\0';
    return 1;
}

/* Kernel dev_t packing, as the filter reads it; glibc st_dev differs. */
static uint32_t kernel_dev(dev_t glibc_dev)
{
    return (uint32_t)((major(glibc_dev) << 20) | minor(glibc_dev));
}

/* Must match the filter: sysfs keys use a sentinel dev, each netns has its own s_dev. */
#define GH_SYSFS_MAGIC 0x62656572
#define GH_SYSFS_DEV 0xffffffffu

/* Must match GH_NAME_LEN in the filter: the kernel hashes raw key bytes. */
#define GH_NAME_LEN 32

struct gh_dir_key
{
    char name[GH_NAME_LEN];
};

/* Hide one entry name from getdents64: libpci exit()s on a listed device it cannot read. */
static int add_dirent_key(int map_fd, const char *name, int *count)
{
    if (map_fd < 0 || !name || !*name)
        return 0;
    if (strlen(name) >= GH_NAME_LEN)
        return 0;

    struct gh_dir_key key = {0};
    /* Length checked above, so the name and its terminator fit. */
    memcpy(key.name, name, strlen(name));

    uint8_t val = 1;
    if (p_map_update(map_fd, &key, &val, 0) != 0)
        return 0;
    if (count)
        (*count)++;
    return 1;
}

static int add_key(int map_fd, const char *path, int follow, int *count, uint8_t kind)
{
    struct stat st;
    if ((follow ? stat(path, &st) : lstat(path, &st)) != 0)
        return 0;

    struct gh_file_key key = {0};
    key.dev = kernel_dev(st.st_dev);
    key.ino = (uint64_t)st.st_ino;

    /* statfs follows symlinks; for lstat'd symlinks classify by the parent
     * filesystem, which is the one the symlink inode lives on. */
    struct statfs sfs;
    if (statfs(path, &sfs) == 0 && (unsigned long)sfs.f_type == GH_SYSFS_MAGIC)
        key.dev = GH_SYSFS_DEV;

    if (p_map_update(map_fd, &key, &kind, 0) != 0)
    {
        fprintf(stderr, "bpf: map update failed for %s: %s\n", path, strerror(errno));
        return 0;
    }
    if (count)
        (*count)++;
    return 1;
}

/* Class entries whose device sits under the dGPU: block the link and hide its name. */
static void add_class_links(int map_fd, int dirent_fd, const char *cls, const char *devdir,
                            int *count, int *hidden)
{
    DIR *d = opendir(cls);
    if (!d)
        return;
    size_t len = strlen(devdir);
    struct dirent *e;
    while ((e = readdir(d)))
    {
        if (e->d_name[0] == '.')
            continue;
        char path[PATH_BUF_SIZE], real[PATH_MAX];
        snprintf(path, sizeof(path), "%s/%s", cls, e->d_name);
        if (!realpath(path, real) || strncmp(real, devdir, len) != 0 || real[len] != '/')
            continue;
        add_key(map_fd, path, 0, count, GH_INO_DGPU);
        add_dirent_key(dirent_fd, e->d_name, hidden);
    }
    closedir(d);
}

/* True when every PCI device directly in dir is `below` or a hidden dGPU
 * function (.0 or .1 of the dGPU's slot). */
static int only_hidden_children(const char *dir, const char *below, const char *bdf)
{
    DIR *d = opendir(dir);
    if (!d)
        return 0;
    int ok = 1;
    struct dirent *e;
    while (ok && (e = readdir(d)))
    {
        if (!valid_bdf(e->d_name) || strcmp(e->d_name, below) == 0)
            continue;
        ok = strncmp(e->d_name, bdf, 11) == 0 && (e->d_name[11] == '0' || e->d_name[11] == '1');
    }
    closedir(d);
    return ok;
}

/* Bridges above the dGPU that lead to nothing else, so a PCI listing does not wake them. */
static void add_parent_bridges(int map_fd, int dirent_fd, const char *devdir, const char *bdf,
                               int *count, int *hidden)
{
    char cur[PATH_MAX], below[16];
    snprintf(cur, sizeof(cur), "%s", devdir);
    snprintf(below, sizeof(below), "%s", bdf);
    for (int depth = 0; depth < 8; depth++)
    {
        char *slash = strrchr(cur, '/');
        if (!slash)
            return;
        *slash = '\0';
        const char *name = strrchr(cur, '/');
        name = name ? name + 1 : cur;
        if (!valid_bdf(name) || !only_hidden_children(cur, below, bdf))
            return;

        char path[PATH_BUF_SIZE];
        snprintf(path, sizeof(path), "/sys/bus/pci/devices/%.12s", name);
        add_key(map_fd, path, 0, count, GH_INO_DGPU);
        add_key(map_fd, cur, 1, count, GH_INO_DGPU);
        add_dirent_key(dirent_fd, name, hidden);
        snprintf(below, sizeof(below), "%.12s", name);
    }
}

/* The card's /dev/nvidiaN minor from the driver's proc file (cached data, no
 * wake); -1 when unknown. */
static int nvidia_minor(const char *bdf)
{
    char path[PATH_BUF_SIZE], line[256];
    snprintf(path, sizeof(path), "/proc/driver/nvidia/gpus/%s/information", bdf);
    FILE *f = fopen(path, "r");
    if (!f)
        return -1;
    int minor = -1;
    while (minor < 0 && fgets(line, sizeof(line), f))
        if (sscanf(line, "Device Minor: %d", &minor) != 1)
            minor = -1;
    fclose(f);
    return minor;
}

/* iGPU nodes, denied to forced processes only; needs a filter with the env map pinned. */
static void add_igpu_nodes(int map_fd, const char *bdf)
{
    if (access(PIN_ENV, F_OK) != 0)
        return;
    DIR *d = opendir("/sys/bus/pci/devices");
    if (!d)
        return;
    struct dirent *e;
    while ((e = readdir(d)))
    {
        char path[PATH_BUF_SIZE], val[32];
        if (!valid_bdf(e->d_name) || strcmp(e->d_name, bdf) == 0)
            continue;
        snprintf(path, sizeof(path), "/sys/bus/pci/devices/%s/boot_vga", e->d_name);
        if (!read_line(path, val, sizeof(val)) || strcmp(val, "1") != 0)
            continue;
        snprintf(path, sizeof(path), "/sys/bus/pci/devices/%s/drm", e->d_name);
        DIR *drm = opendir(path);
        struct dirent *n;
        while (drm && (n = readdir(drm)))
        {
            if (strncmp(n->d_name, "card", 4) != 0 && strncmp(n->d_name, "renderD", 7) != 0)
                continue;
            snprintf(path, sizeof(path), "/dev/dri/%s", n->d_name);
            add_key(map_fd, path, 1, NULL, GH_INO_IGPU);
        }
        if (drm)
            closedir(drm);
    }
    closedir(d);
}

/* Resolve every file to hide for this GPU; the resolved PCI dir covers lazy sysfs attributes. */
static int populate_blocked(int map_fd, const char *bdf)
{
    char path[PATH_BUF_SIZE];
    int count = 0;

    /* Names to splice out of directory listings. Absent pin means an older
     * filter is attached, in which case only the inode blocks apply. */
    int dirent_fd = p_obj_get(PIN_DIRENT);
    int hidden = 0;

    /* PCI device dir: symlink and resolved target. */
    snprintf(path, sizeof(path), "/sys/bus/pci/devices/%s", bdf);
    add_key(map_fd, path, 0, &count, GH_INO_DGPU);
    add_key(map_fd, path, 1, &count, GH_INO_DGPU);
    add_dirent_key(dirent_fd, bdf, &hidden);
    char devdir[PATH_MAX];
    if (realpath(path, devdir))
    {
        add_parent_bridges(map_fd, dirent_fd, devdir, bdf, &count, &hidden);
        static const char *const CLASSES[] = {
            "/sys/class/drm", "/sys/class/hwmon", "/sys/class/backlight", NULL};
        for (int i = 0; CLASSES[i]; i++)
            add_class_links(map_fd, dirent_fd, CLASSES[i], devdir, &count, &hidden);
    }

    /* Audio function of the same device. */
    char audio[PATH_BUF_SIZE];
    snprintf(audio, sizeof(audio), "%s", bdf);
    size_t alen = strlen(audio);
    if (alen >= 1)
    {
        audio[alen - 1] = '1';
        snprintf(path, sizeof(path), "/sys/bus/pci/devices/%s", audio);
        add_key(map_fd, path, 0, &count, GH_INO_DGPU);
        add_key(map_fd, path, 1, &count, GH_INO_DGPU);
        add_dirent_key(dirent_fd, audio, &hidden);
    }

    /* DRM nodes owned by this device: /sys/bus/pci/devices/<bdf>/drm holds
     * cardN and renderDN, which name the /dev/dri nodes to hide. */
    snprintf(path, sizeof(path), "/sys/bus/pci/devices/%s/drm", bdf);
    DIR *d = opendir(path);
    if (d)
    {
        struct dirent *e;
        while ((e = readdir(d)))
        {
            if (e->d_name[0] == '.')
                continue;
            if (strncmp(e->d_name, "card", 4) != 0 && strncmp(e->d_name, "renderD", 7) != 0)
                continue;

            snprintf(path, sizeof(path), "/dev/dri/%s", e->d_name);
            add_key(map_fd, path, 1, &count, GH_INO_DGPU);

            /* Hides the node in /dev/dri, which is what EGL and Vulkan
             * loaders walk; the /sys/class/drm entries are class links above. */
            add_dirent_key(dirent_fd, e->d_name, &hidden);
        }
        closedir(d);
    }

    /* NVIDIA nodes plus nvidiactl (opening it can resume the dGPU); not modeset, uvm or caps. */
    char vendor[32] = {0};
    snprintf(path, sizeof(path), "/sys/bus/pci/devices/%s/vendor", bdf);
    int vfd = open(path, O_RDONLY);
    if (vfd >= 0)
    {
        ssize_t n = read(vfd, vendor, sizeof(vendor) - 1);
        close(vfd);
        if (n > 0)
            vendor[n] = '\0';
    }
    if (strncmp(vendor, "0x10de", 6) == 0)
    {
        int minor = nvidia_minor(bdf);
        d = opendir("/dev");
        if (d)
        {
            struct dirent *e;
            while ((e = readdir(d)))
            {
                if (strncmp(e->d_name, "nvidia", 6) != 0)
                    continue;
                /* nvidia0..N and nvidiactl. Skip nvidia-uvm: unloading it is
                 * not needed and CUDA apps handle its absence badly. */
                const char *tail = e->d_name + 6;
                int numeric = tail[0] != '\0';
                for (const char *c = tail; *c; c++)
                    if (!isdigit((unsigned char)*c))
                        numeric = 0;
                if (!numeric && strcmp(e->d_name, "nvidiactl") != 0)
                    continue;
                if (numeric && minor >= 0 && atoi(tail) != minor)
                    continue;

                snprintf(path, sizeof(path), "/dev/%s", e->d_name);
                add_key(map_fd, path, 1, &count, GH_INO_DGPU);
                add_dirent_key(dirent_fd, e->d_name, &hidden);
            }
            closedir(d);
        }

        /* Vulkan ICD manifest: hiding it saves a wasted driver load; the EGL file stays for compositors. */
        static const char *const ICD_DIRS[] = {
            "/usr/share/vulkan/icd.d", "/etc/vulkan/icd.d",
            "/run/opengl-driver/share/vulkan/icd.d",
            "/run/opengl-driver-32/share/vulkan/icd.d", NULL};
        static const char *const ICD_NAMES[] = {
            "nvidia_icd.json", "nvidia_icd.x86_64.json", "nvidia_icd.i686.json", NULL};
        for (int i = 0; ICD_DIRS[i]; i++)
        {
            for (int j = 0; ICD_NAMES[j]; j++)
            {
                snprintf(path, sizeof(path), "%s/%s", ICD_DIRS[i], ICD_NAMES[j]);
                if (access(path, F_OK) != 0)
                    continue;
                add_key(map_fd, path, 1, &count, GH_INO_DGPU);
                add_dirent_key(dirent_fd, ICD_NAMES[j], &hidden);
            }
        }
    }

    if (dirent_fd >= 0)
    {
        glog(LOG_INFO, "bpf-block: hiding %d dirent name(s) from listings", hidden);
        close(dirent_fd);
    }

    add_igpu_nodes(map_fd, bdf);
    return count;
}

static int populate_comms(int map_fd)
{
    int count = 0;
    for (int i = 0; ALLOWED_COMMS[i]; i++)
    {
        char key[GH_COMM_LEN] = {0};
        strncpy(key, ALLOWED_COMMS[i], sizeof(key) - 1);
        uint8_t val = 1;
        if (p_map_update(map_fd, key, &val, 0) == 0)
            count++;
    }

    char user[USER_COMMS_MAX][GH_COMM_LEN];
    int n = read_user_comms(user);
    for (int i = 0; i < n; i++)
    {
        uint8_t val = GH_COMM_USER;
        if (p_map_update(map_fd, user[i], &val, 0) == 0)
            count++;
    }
    return count;
}

static int env_put(int map_fd, const char *entry, uint8_t level)
{
    char key[GH_ENV_LEN] = {0};
    if (strlen(entry) >= GH_ENV_LEN)
        return 0;
    memcpy(key, entry, strlen(entry));
    return p_map_update(map_fd, key, &level, 0) == 0;
}

/* Built-in and user environment entries. DRI_PRIME also names the card by
 * address (switcheroo-control) or by vendor:device ids. */
static int populate_envs(int map_fd, const char *bdf)
{
    int count = 0;
    for (int i = 0; ALLOWED_ENVS[i]; i++)
        count += env_put(map_fd, ALLOWED_ENVS[i], GH_PID_ALLOW);
    for (int i = 0; FORCED_ENVS[i]; i++)
        count += env_put(map_fd, FORCED_ENVS[i], GH_PID_FORCE);

    char entry[GH_ENV_LEN], addr[16], path[PATH_BUF_SIZE], vendor[16], device[16];
    snprintf(addr, sizeof(addr), "%s", bdf);
    for (char *c = addr; *c; c++)
        if (*c == ':' || *c == '.')
            *c = '_';
    snprintf(entry, sizeof(entry), "DRI_PRIME=pci-%s", addr);
    count += env_put(map_fd, entry, GH_PID_ALLOW);
    snprintf(path, sizeof(path), "/sys/bus/pci/devices/%s/vendor", bdf);
    int ok = read_line(path, vendor, sizeof(vendor));
    snprintf(path, sizeof(path), "/sys/bus/pci/devices/%s/device", bdf);
    if (ok && read_line(path, device, sizeof(device)) && strlen(vendor) == 6 && strlen(device) == 6)
    {
        snprintf(entry, sizeof(entry), "DRI_PRIME=%s:%s", vendor + 2, device + 2);
        count += env_put(map_fd, entry, GH_PID_ALLOW);
    }

    char user[USER_ENVS_MAX][GH_ENV_LEN];
    int n = read_user_envs(user);
    for (int i = 0; i < n; i++)
        count += env_put(map_fd, user[i], GH_PID_ALLOW);
    return count;
}

/* ---------- runtime power management ---------- */

/* D3cold also needs runtime PM on and the nvidia daemons gone; changes are recorded under /run. */
#define PM_STATE_DIR "/run/ghelper"
#define PM_STATE_FILE PM_STATE_DIR "/bpf-block-pm.state"

static const char *const NVIDIA_DAEMONS[] = {
    "nvidia-powerd.service", "nvidia-persistenced.service", NULL};

static int run_quiet(const char *const argv[])
{
    pid_t pid = fork();
    if (pid < 0)
        return -1;
    if (pid == 0)
    {
        int devnull = open("/dev/null", O_RDWR);
        if (devnull >= 0)
        {
            dup2(devnull, 1);
            dup2(devnull, 2);
            close(devnull);
        }
        execvp(argv[0], (char *const *)argv);
        _exit(127);
    }
    int status = 0;
    if (waitpid(pid, &status, 0) < 0)
        return -1;
    return WIFEXITED(status) ? WEXITSTATUS(status) : -1;
}

static int unit_active(const char *unit)
{
    const char *argv[] = {"systemctl", "--quiet", "is-active", unit, NULL};
    return run_quiet(argv) == 0;
}

static void unit_ctl(const char *verb, const char *unit)
{
    const char *argv[] = {"systemctl", verb, unit, NULL};
    run_quiet(argv);
}

static void write_str(const char *path, const char *val)
{
    int fd = open(path, O_WRONLY);
    if (fd < 0)
        return;
    (void)!write(fd, val, strlen(val));
    close(fd);
}

static int dgpu_is_nvidia(const char *bdf)
{
    char path[PATH_BUF_SIZE], vendor[16] = {0};
    snprintf(path, sizeof(path), "/sys/bus/pci/devices/%s/vendor", bdf);
    return read_line(path, vendor, sizeof(vendor)) && strncmp(vendor, "0x10de", 6) == 0;
}

/* Which card `on` blocked, so `off` can announce the release without being
 * told the address again. */
#define BDF_FILE PM_STATE_DIR "/bpf-block.bdf"

/* True when a DRM card other than bdf exists; removing the only GPU makes KWin exit. */
static int other_drm_card(const char *bdf)
{
    DIR *d = opendir("/sys/class/drm");
    if (!d)
        return 0;
    struct dirent *e;
    char path[PATH_BUF_SIZE], target[PATH_BUF_SIZE];
    int found = 0;
    while (!found && (e = readdir(d)))
    {
        if (strncmp(e->d_name, "card", 4) != 0 || strchr(e->d_name, '-'))
            continue;
        snprintf(path, sizeof(path), "/sys/class/drm/%s/device", e->d_name);
        ssize_t n = readlink(path, target, sizeof(target) - 1);
        if (n <= 0)
            continue;
        target[n] = '\0';
        const char *base = strrchr(target, '/');
        found = strcmp(base ? base + 1 : target, bdf) != 0;
    }
    closedir(d);
    return found;
}

/* Announce the card as removed (on) or added (off), so compositors close or reopen it. Root only. */
static void drm_uevent(const char *bdf, const char *action)
{
    char path[PATH_BUF_SIZE];
    snprintf(path, sizeof(path), "/sys/bus/pci/devices/%s/drm", bdf);
    DIR *d = opendir(path);
    if (!d)
        return;
    struct dirent *e;
    while ((e = readdir(d)))
    {
        if (strncmp(e->d_name, "card", 4) != 0)
            continue;
        snprintf(path, sizeof(path), "/sys/class/drm/%s/uevent", e->d_name);
        write_str(path, action);
    }
    closedir(d);
}

/* Open the runtime PM gate and park the nvidia daemons; an existing state file is kept. */
static void pm_apply(const char *bdf)
{
    int fresh = access(PM_STATE_FILE, F_OK) != 0;
    FILE *state = NULL;
    if (fresh)
    {
        mkdir(PM_STATE_DIR, 0755);
        state = fopen(PM_STATE_FILE, "w");
    }

    /* GPU function and its audio sibling. */
    for (int fn = 0; fn <= 1; fn++)
    {
        char dev[64];
        snprintf(dev, sizeof(dev), "%s", bdf);
        size_t dlen = strlen(dev);
        if (dlen >= 1)
            dev[dlen - 1] = (char)('0' + fn);

        char path[PATH_BUF_SIZE], old[32] = {0};
        snprintf(path, sizeof(path), "/sys/bus/pci/devices/%s/power/control", dev);
        if (!read_line(path, old, sizeof(old)))
            continue;
        if (strcmp(old, "auto") != 0)
        {
            if (state)
                fprintf(state, "control %s %s\n", dev, old);
            write_str(path, "auto");
        }
    }

    if (dgpu_is_nvidia(bdf))
    {
        for (int i = 0; NVIDIA_DAEMONS[i]; i++)
        {
            if (!unit_active(NVIDIA_DAEMONS[i]))
                continue;
            if (state)
                fprintf(state, "service %s\n", NVIDIA_DAEMONS[i]);
            unit_ctl("stop", NVIDIA_DAEMONS[i]);
        }
    }

    if (state)
        fclose(state);
}

/* Undo pm_apply, then start enabled nvidia daemons that are not running. */
static void pm_restore(void)
{
    FILE *state = fopen(PM_STATE_FILE, "r");
    if (state)
    {
        char kind[16], a[128], b[32];
        while (1)
        {
            int n = fscanf(state, "%15s %127s", kind, a);
            if (n != 2)
                break;
            if (strcmp(kind, "control") == 0)
            {
                if (fscanf(state, "%31s", b) != 1)
                    break;
                char path[PATH_BUF_SIZE];
                snprintf(path, sizeof(path), "/sys/bus/pci/devices/%s/power/control", a);
                write_str(path, b);
            }
            else if (strcmp(kind, "service") == 0)
            {
                unit_ctl("start", a);
            }
        }
        fclose(state);
        unlink(PM_STATE_FILE);
    }

    for (int i = 0; NVIDIA_DAEMONS[i]; i++)
    {
        const char *enabled[] = {"systemctl", "--quiet", "is-enabled", NVIDIA_DAEMONS[i], NULL};
        if (run_quiet(enabled) != 0 || unit_active(NVIDIA_DAEMONS[i]))
            continue;
        unit_ctl("reset-failed", NVIDIA_DAEMONS[i]);
        unit_ctl("start", NVIDIA_DAEMONS[i]);
    }
}

/* Name and parent from /proc/<pid>/stat; the name may hold ')', so parse from the last one. */
static int read_proc_stat(int pid, char comm[GH_COMM_LEN], int *ppid)
{
    char path[64], buf[512];
    snprintf(path, sizeof(path), "/proc/%d/stat", pid);
    if (!read_line(path, buf, sizeof(buf)))
        return 0;
    char *open_p = strchr(buf, '(');
    char *close_p = strrchr(buf, ')');
    if (!open_p || !close_p || close_p < open_p || sscanf(close_p + 1, " %*c %d", ppid) != 1)
        return 0;
    size_t len = (size_t)(close_p - open_p - 1);
    if (len >= GH_COMM_LEN)
        len = GH_COMM_LEN - 1;
    memset(comm, 0, GH_COMM_LEN);
    memcpy(comm, open_p + 1, len);
    return 1;
}

/* Highest level an environment entry of pid asks for, from /proc/<pid>/environ. */
static int env_level_of(int pid, char envs[][GH_ENV_LEN], const uint8_t *levels, int ne)
{
    static char buf[65536];
    char path[64];
    if (ne == 0)
        return 0;
    snprintf(path, sizeof(path), "/proc/%d/environ", pid);
    int fd = open(path, O_RDONLY);
    if (fd < 0)
        return 0;
    ssize_t len = read(fd, buf, sizeof(buf) - 1);
    close(fd);
    if (len <= 0)
        return 0;
    buf[len] = '\0';

    int level = 0;
    for (char *p = buf; p < buf + len; p += strlen(p) + 1)
        for (int i = 0; i < ne; i++)
            if (levels[i] > level && strcmp(p, envs[i]) == 0)
                level = levels[i];
    return level;
}

/* What the filter would grant pid: its own environment, or a user pick on its
 * parent chain. */
static int chain_level(int pid, char comms[][GH_COMM_LEN], int nc,
                       char envs[][GH_ENV_LEN], const uint8_t *levels, int ne)
{
    int level = env_level_of(pid, envs, levels, ne);
    int cur = pid, ppid = 0;
    for (int depth = 0; !level && nc > 0 && cur > 1 && depth < 64; depth++, cur = ppid)
    {
        char comm[GH_COMM_LEN];
        if (!read_proc_stat(cur, comm, &ppid))
            break;
        for (int i = 0; i < nc && !level; i++)
            if (strcmp(comms[i], comm) == 0)
                level = GH_PID_ALLOW;
    }
    return level;
}

/* The live environment entries and their levels. */
static int read_env_map(char out[][GH_ENV_LEN], uint8_t *levels, int max)
{
    int fd = p_obj_get(PIN_ENV);
    if (fd < 0)
        return 0;
    int n = 0;
    char key[GH_ENV_LEN], next[GH_ENV_LEN];
    int have = p_map_next_key(fd, NULL, next) == 0;
    while (have && n < max)
    {
        memcpy(key, next, sizeof(key));
        if (p_map_lookup(fd, key, &levels[n]) == 0)
        {
            memcpy(out[n], key, GH_ENV_LEN);
            out[n][GH_ENV_LEN - 1] = '\0';
            n++;
        }
        have = p_map_next_key(fd, key, next) == 0;
    }
    close(fd);
    return n;
}

/* Record running processes the filter would have allowed at exec, which
 * started before the hooks. Never lowers an existing record. */
static int seed_pids(void)
{
    /* Without the exit hook a record outlives its process and passes to a reused pid. */
    if (access(PIN_DIR "/link_gh_exit", F_OK) != 0)
        return 0;
    char comms[USER_COMMS_MAX][GH_COMM_LEN];
    int nc = read_user_comms(comms);
    static char envs[128][GH_ENV_LEN];
    uint8_t levels[128];
    int ne = read_env_map(envs, levels, 128);
    int fd = nc + ne > 0 ? p_obj_get(PIN_PID) : -1;
    if (fd < 0)
        return 0;

    int count = 0;
    DIR *d = opendir("/proc");
    struct dirent *e;
    while (d && (e = readdir(d)))
    {
        int pid = atoi(e->d_name);
        if (pid <= 0)
            continue;
        uint8_t level = (uint8_t)chain_level(pid, comms, nc, envs, levels, ne), old = 0;
        uint32_t key = (uint32_t)pid;
        if (!level || (p_map_lookup(fd, &key, &old) == 0 && old >= level))
            continue;
        if (p_map_update(fd, &key, &level, 0) == 0)
            count++;
    }
    if (d)
        closedir(d);
    close(fd);
    return count;
}

/* After a user entry is removed: drop the records it alone granted. Records
 * from other entries or from allow-pid stay. */
static void prune_pids(const char *removed)
{
    int fd = p_obj_get(PIN_PID);
    if (fd < 0)
        return;
    char comms[USER_COMMS_MAX][GH_COMM_LEN];
    int nc = read_user_comms(comms);
    static char envs[128][GH_ENV_LEN];
    uint8_t levels[128];
    int ne = read_env_map(envs, levels, 128);

    char one_comm[1][GH_COMM_LEN] = {{0}};
    char one_env[1][GH_ENV_LEN] = {{0}};
    uint8_t one_level = GH_PID_ALLOW;
    int is_env = strchr(removed, '=') != NULL;
    snprintf(is_env ? one_env[0] : one_comm[0], is_env ? GH_ENV_LEN : GH_COMM_LEN, "%s", removed);

    static uint32_t pids[8192];
    int n = 0;
    uint32_t key = 0, next = 0;
    int have = p_map_next_key(fd, NULL, &next) == 0;
    while (have && n < 8192)
    {
        pids[n++] = key = next;
        have = p_map_next_key(fd, &key, &next) == 0;
    }
    for (int i = 0; i < n; i++)
    {
        int pid = (int)pids[i];
        int related = is_env ? chain_level(pid, NULL, 0, one_env, &one_level, 1)
                             : chain_level(pid, one_comm, 1, NULL, NULL, 0);
        if (related && !chain_level(pid, comms, nc, envs, levels, ne))
            p_map_delete(fd, &pids[i]);
    }
    close(fd);
}

/* ---------- attach / detach ---------- */

static int mkdir_p_pin(void)
{
    if (mkdir(PIN_DIR, 0700) != 0 && errno != EEXIST)
    {
        fprintf(stderr, "bpf: mkdir %s: %s\n", PIN_DIR, strerror(errno));
        return 0;
    }
    return 1;
}

/* Listing programs; lockdown=integrity refuses probe_write_user and fails the whole load. */
static const char *const LISTING_PROGS[] = {"gh_getdents_enter", "gh_getdents_exit", NULL};

static struct bpf_object *open_and_load(int listings)
{
    struct bpf_object *obj = p_object_open_mem(gh_bpf_obj, gh_bpf_obj_len, NULL);
    if (!obj)
    {
        fprintf(stderr, "bpf: open object failed: %s\n", strerror(errno));
        return NULL;
    }
    for (int i = 0; !listings && LISTING_PROGS[i]; i++)
    {
        struct bpf_program *prog = p_find_prog(obj, LISTING_PROGS[i]);
        if (prog)
            p_set_autoload(prog, false);
    }
    if (p_object_load(obj) != 0)
    {
        fprintf(stderr, "bpf: load%s failed: %s (lockdown=%s)\n",
                listings ? "" : " (without listings)", strerror(errno), lockdown_state());
        p_object_close(obj);
        return NULL;
    }
    return obj;
}

/* Load the filter, attach its hooks, and pin links plus maps so the
 * attachment survives this process exiting. */
static int load_and_pin(void)
{
    int listings = 1;
    struct bpf_object *obj = open_and_load(1);
    if (!obj && p_set_autoload)
    {
        listings = 0;
        obj = open_and_load(0);
    }
    if (!obj)
        return 0;
    if (!listings)
        glog(LOG_WARNING, "bpf-block: listing hooks refused, entries stay listed");

    static const char *const progs[] = {
        "gh_file_open", "gh_inode_permission", "gh_inode_getattr", NULL};

    int ok = 1;
    for (int i = 0; progs[i] && ok; i++)
    {
        struct bpf_program *prog = p_find_prog(obj, progs[i]);
        if (!prog)
        {
            fprintf(stderr, "bpf: program %s not found\n", progs[i]);
            ok = 0;
            break;
        }
        struct bpf_link *link = p_attach_lsm(prog);
        if (!link)
        {
            fprintf(stderr, "bpf: attach %s failed: %s\n", progs[i], strerror(errno));
            ok = 0;
            break;
        }
        char pin[PATH_BUF_SIZE];
        snprintf(pin, sizeof(pin), PIN_DIR "/link_%s", progs[i]);
        unlink(pin);
        if (p_link_pin(link, pin) != 0)
        {
            fprintf(stderr, "bpf: pin %s failed: %s\n", pin, strerror(errno));
            ok = 0;
            break;
        }
    }

    /* getdents64 tracepoints hide blocked entries; best effort. */
    if (ok && listings && p_attach_tracepoint)
    {
        static const char *const tps[][2] = {
            {"gh_getdents_enter", "sys_enter_getdents64"},
            {"gh_getdents_exit", "sys_exit_getdents64"},
            {NULL, NULL}};

        for (int i = 0; tps[i][0]; i++)
        {
            struct bpf_program *prog = p_find_prog(obj, tps[i][0]);
            if (!prog)
                continue;
            struct bpf_link *link = p_attach_tracepoint(prog, "syscalls", tps[i][1]);
            if (!link)
            {
                fprintf(stderr, "bpf: attach %s failed: %s (listings not filtered)\n",
                        tps[i][0], strerror(errno));
                continue;
            }
            char pin[PATH_BUF_SIZE];
            snprintf(pin, sizeof(pin), PIN_DIR "/link_%s", tps[i][0]);
            unlink(pin);
            if (p_link_pin(link, pin) != 0)
                fprintf(stderr, "bpf: pin %s failed: %s\n", pin, strerror(errno));
        }
    }

    /* Exit first and stop at a failure: no record without the hook that removes it. */
    static const char *const raw_tps[][2] = {
        {"gh_exit", "sched_process_exit"},
        {"gh_exec", "sched_process_exec"},
        {"gh_fork", "sched_process_fork"},
        {NULL, NULL}};

    for (int i = 0; ok && p_attach_raw_tp && raw_tps[i][0]; i++)
    {
        struct bpf_program *prog = p_find_prog(obj, raw_tps[i][0]);
        struct bpf_link *link = prog ? p_attach_raw_tp(prog, raw_tps[i][1]) : NULL;
        if (!link)
        {
            fprintf(stderr, "bpf: attach %s failed: %s (name matching only)\n",
                    raw_tps[i][0], strerror(errno));
            break;
        }
        char pin[PATH_BUF_SIZE];
        snprintf(pin, sizeof(pin), PIN_DIR "/link_%s", raw_tps[i][0]);
        unlink(pin);
        if (p_link_pin(link, pin) != 0)
            fprintf(stderr, "bpf: pin %s failed: %s\n", pin, strerror(errno));
    }

    static const char *const maps[][2] = {
        {"gh_mode", PIN_MODE},
        {"gh_blocked_ino", PIN_BLOCKED},
        {"gh_allowed_comm", PIN_COMM},
        {"gh_hidden_dirent", PIN_DIRENT},
        {"gh_allowed_pid", PIN_PID},
        {"gh_allowed_env", PIN_ENV},
        {"gh_refused", PIN_REFUSED},
        {NULL, NULL}};

    for (int i = 0; maps[i][0] && ok; i++)
    {
        struct bpf_map *map = p_find_map(obj, maps[i][0]);
        if (!map)
        {
            fprintf(stderr, "bpf: map %s not found\n", maps[i][0]);
            ok = 0;
            break;
        }
        unlink(maps[i][1]);
        if (p_map_pin(map, maps[i][1]) != 0)
        {
            fprintf(stderr, "bpf: pin map %s failed: %s\n", maps[i][1], strerror(errno));
            ok = 0;
        }
    }

    /* Closing the object frees the in-process handles. The pins keep the
     * programs attached and the maps alive. */
    p_object_close(obj);

    if (!ok)
    {
        glog(LOG_ERR, "bpf-block: load/attach failed");
        return 0;
    }
    glog(LOG_INFO, "bpf-block: filter attached and pinned");
    return 1;
}

static int set_mode(uint8_t on)
{
    int fd = p_obj_get(PIN_MODE);
    if (fd < 0)
        return 0;
    uint32_t key = 0;
    int rc = p_map_update(fd, &key, &on, 0);
    close(fd);
    if (rc != 0)
    {
        fprintf(stderr, "bpf: mode update failed: %s\n", strerror(errno));
        return 0;
    }
    return 1;
}

/* Largest map key: get_next_key writes the full key size. */
#define GH_MAX_KEY_LEN 64
_Static_assert(sizeof(struct gh_file_key) <= GH_MAX_KEY_LEN, "file key fits");
_Static_assert(sizeof(struct gh_dir_key) <= GH_MAX_KEY_LEN, "dir key fits");

static void clear_map(const char *pin)
{
    int fd = p_obj_get(pin);
    if (fd < 0)
        return;

    /* Always ask for the first key rather than iterating: the previous key is
     * gone after the delete, which would invalidate a normal walk. */
    unsigned char first[GH_MAX_KEY_LEN] = {0};
    while (p_map_next_key(fd, NULL, first) == 0)
        if (p_map_delete(fd, first) != 0)
            break;
    close(fd);
}

static void remove_pins(void)
{
    DIR *d = opendir(PIN_DIR);
    if (!d)
        return;
    struct dirent *e;
    char path[PATH_BUF_SIZE];
    while ((e = readdir(d)))
    {
        if (e->d_name[0] == '.')
            continue;
        snprintf(path, sizeof(path), PIN_DIR "/%s", e->d_name);
        unlink(path);
    }
    closedir(d);
    rmdir(PIN_DIR);
}

/* ---------- subcommands ---------- */

static int bpf_block_on(const char *bdf)
{
    if (!lsm_bpf_active())
    {
        fprintf(stderr, "bpf-block: bpf is not in the active LSM list "
                        "(see /sys/kernel/security/lsm); reboot with it enabled\n");
        return 4;
    }
    if (gh_bpf_obj_len == 0)
    {
        fprintf(stderr, "bpf-block: filter not built into this helper\n");
        return 4;
    }
    if (!load_libbpf())
    {
        fprintf(stderr, "bpf-block: libbpf not available\n");
        return 4;
    }
    /* systemd mounts bpffs at boot; mount it here when something did not. */
    if (!bpffs_mounted() && mount("bpf", BPF_FS_DIR, "bpf", 0, NULL) != 0)
        glog(LOG_WARNING, "bpf-block: mount bpf on %s failed: %s", BPF_FS_DIR, strerror(errno));
    if (!bpffs_mounted())
    {
        fprintf(stderr, "bpf-block: %s is not a bpf filesystem\n", BPF_FS_DIR);
        return 4;
    }
    if (!mkdir_p_pin())
        return 3;

    /* Reuse an existing attachment; only load when nothing is pinned yet. */
    if (access(PIN_MODE, F_OK) != 0 && !load_and_pin())
        return 3;

    int map_fd = p_obj_get(PIN_BLOCKED);
    if (map_fd < 0)
    {
        fprintf(stderr, "bpf-block: cannot open %s: %s\n", PIN_BLOCKED, strerror(errno));
        return 3;
    }
    int files = populate_blocked(map_fd, bdf);
    close(map_fd);

    int comm_fd = p_obj_get(PIN_COMM);
    int comms = 0;
    if (comm_fd >= 0)
    {
        comms = populate_comms(comm_fd);
        close(comm_fd);
    }
    int env_fd = p_obj_get(PIN_ENV);
    if (env_fd >= 0)
    {
        populate_envs(env_fd, bdf);
        close(env_fd);
    }

    if (files == 0)
    {
        fprintf(stderr, "bpf-block: no files resolved for %s, refusing to enable\n", bdf);
        return 3;
    }
    if (!set_mode(1))
        return 3;
    int pids = seed_pids();

    /* New opens are denied from here on; now drop what keeps the card awake. */
    pm_apply(bdf);

    mkdir(PM_STATE_DIR, 0755);
    FILE *bf = fopen(BDF_FILE, "w");
    if (bf)
    {
        fprintf(bf, "%s\n", bdf);
        fclose(bf);
    }
    /* KWin 6.8 keeps every GPU open; a remove makes it, logind and PID 1 close the card. */
    if (other_drm_card(bdf))
        drm_uevent(bdf, "remove\n");

    glog(LOG_INFO, "bpf-block on %s: %d files, %d comms, %d pids", bdf, files, comms, pids);
    printf("blocked=%d comms=%d\n", files, comms);
    return 0;
}

static int bpf_block_off(void)
{
    if (!load_libbpf())
    {
        fprintf(stderr, "bpf-block: libbpf not available\n");
        return 4;
    }
    /* Clear the gate first so access is restored immediately, then detach. */
    if (access(PIN_MODE, F_OK) == 0)
        set_mode(0);

    clear_map(PIN_BLOCKED);
    clear_map(PIN_DIRENT);
    remove_pins();

    /* Access is restored; bring back what pm_apply parked. */
    pm_restore();

    char bdf[32] = {0};
    if (read_line(BDF_FILE, bdf, sizeof(bdf)) && valid_bdf(bdf))
        drm_uevent(bdf, "add\n");
    unlink(BDF_FILE);

    glog(LOG_INFO, "bpf-block off: detached");
    printf("blocked=0\n");
    return 0;
}

/* Remove all the eBPF backend added, including the LSM change. Exits as lsm-disable. */
static int bpf_block_uninstall(void)
{
    if (access(PIN_DIR, F_OK) == 0)
    {
        if (load_libbpf())
            bpf_block_off();
        else
            remove_pins();
    }
    unlink(USER_COMMS_FILE);

    /* Markers only mean ebpf when the backend marker says so; optimized is ebpf-only. */
    char backend[16] = {0}, persist[16] = {0};
    read_line("/etc/ghelper/backend", backend, sizeof(backend));
    read_line("/etc/ghelper/persistent-gpu-mode", persist, sizeof(persist));
    if (strcmp(backend, "ebpf") == 0)
    {
        unlink("/etc/ghelper/backend");
        unlink("/etc/ghelper/pending-gpu-mode");
        unlink("/etc/ghelper/persistent-gpu-mode");
    }
    else if (strcmp(persist, "optimized") == 0)
        unlink("/etc/ghelper/persistent-gpu-mode");

    glog(LOG_INFO, "bpf-block uninstall: filter, allowed apps and ebpf markers removed");
    return do_lsm_disable(0, NULL);
}

static int bpf_block_status(void)
{
    if (access(PIN_MODE, F_OK) != 0)
    {
        printf("attached=0 mode=0 blocked=0\n");
        return 0;
    }
    if (!load_libbpf())
    {
        printf("attached=1 mode=unknown blocked=unknown\n");
        return 0;
    }

    /* Unknown, not 0: 0 would report an idle filter while it blocks. */
    int mode = -1;
    int fd = p_obj_get(PIN_MODE);
    if (fd >= 0)
    {
        uint32_t key = 0;
        uint8_t val = 0;
        if (p_map_lookup(fd, &key, &val) == 0)
            mode = val;
        close(fd);
    }
    if (mode < 0)
    {
        printf("attached=1 mode=unknown blocked=unknown\n");
        return 3;
    }

    int blocked = 0;
    fd = p_obj_get(PIN_BLOCKED);
    if (fd >= 0)
    {
        struct gh_file_key key = {0};
        struct gh_file_key next = {0};
        int have = p_map_next_key(fd, NULL, &next) == 0;
        while (have)
        {
            blocked++;
            key = next;
            have = p_map_next_key(fd, &key, &next) == 0;
        }
        close(fd);
    }

    printf("attached=1 mode=%d blocked=%d\n", mode, blocked);
    return 0;
}

static int bpf_block_refresh(const char *bdf)
{
    if (access(PIN_MODE, F_OK) != 0)
    {
        fprintf(stderr, "bpf-block: not attached\n");
        return 4;
    }
    if (!load_libbpf())
        return 4;

    /* Stale keys go first: a freed inode number or hwmon name can come back
     * on another device. */
    clear_map(PIN_BLOCKED);
    clear_map(PIN_DIRENT);
    int map_fd = p_obj_get(PIN_BLOCKED);
    if (map_fd < 0)
        return 3;
    int files = populate_blocked(map_fd, bdf);
    close(map_fd);

    glog(LOG_INFO, "bpf-block refresh %s: %d files", bdf, files);
    printf("blocked=%d\n", files);
    return files > 0 ? 0 : 3;
}

/* Print the user list, one per line. Built-ins are not listed: they are not
 * the user's to manage and showing them invites removal attempts. */
static int bpf_block_list_comms(void)
{
    char user[USER_COMMS_MAX][GH_COMM_LEN];
    int n = read_user_comms(user);
    for (int i = 0; i < n; i++)
        printf("%s\n", user[i]);
    char envs[USER_ENVS_MAX][GH_ENV_LEN];
    int ne = read_user_envs(envs);
    for (int i = 0; i < ne; i++)
        printf("%s\n", envs[i]);
    return 0;
}

/* Add or remove name in a list of stride-byte entries. Returns 1 when the
 * list changed, 0 when not, -1 when it is full. */
static int list_set(char *list, size_t stride, int *n, int max, const char *name, int allow)
{
    int at = -1;
    for (int i = 0; i < *n; i++)
        if (strcmp(list + i * stride, name) == 0)
            at = i;
    if (allow == (at >= 0))
        return 0;
    if (allow)
    {
        if (*n >= max)
            return -1;
        memset(list + *n * stride, 0, stride);
        memcpy(list + *n * stride, name, strlen(name));
        (*n)++;
        return 1;
    }
    memmove(list + at * stride, list + (at + 1) * stride, (size_t)(*n - at - 1) * stride);
    (*n)--;
    return 1;
}

/* Add or remove one entry in the file and in a live filter, so the next launch already matches. */
static int bpf_block_set_entry(const char *name, int allow)
{
    int env = strchr(name, '=') != NULL;
    if (env ? !valid_env(name) : !valid_comm(name))
    {
        fprintf(stderr, "bpf-block: invalid entry (process name of 1-%d chars, or KEY=VALUE "
                        "under %d chars, no spaces or slashes)\n",
                GH_COMM_LEN - 1, GH_ENV_LEN);
        return 1;
    }
    if (!env && is_builtin_comm(name))
    {
        fprintf(stderr, "bpf-block: '%s' is always allowed and cannot be changed\n", name);
        return 1;
    }

    char user[USER_COMMS_MAX][GH_COMM_LEN];
    int n = read_user_comms(user);
    char envs[USER_ENVS_MAX][GH_ENV_LEN];
    int ne = read_user_envs(envs);

    int changed = env ? list_set(&envs[0][0], GH_ENV_LEN, &ne, USER_ENVS_MAX, name, allow)
                      : list_set(&user[0][0], GH_COMM_LEN, &n, USER_COMMS_MAX, name, allow);
    if (changed < 0)
    {
        fprintf(stderr, "bpf-block: list is full (%d entries)\n", env ? USER_ENVS_MAX : USER_COMMS_MAX);
        return 1;
    }
    if (changed && !write_user_lists(user, n, envs, ne))
        return 3;

    /* Update a live filter. Absent pins just mean nothing is attached. */
    if (load_libbpf())
    {
        int fd = p_obj_get(env ? PIN_ENV : PIN_COMM);
        if (fd >= 0)
        {
            char key[GH_ENV_LEN] = {0};
            strncpy(key, name, (env ? GH_ENV_LEN : GH_COMM_LEN) - 1);
            if (allow)
            {
                uint8_t val = env ? GH_PID_ALLOW : GH_COMM_USER;
                p_map_update(fd, key, &val, 0);
                close(fd);
                seed_pids();
            }
            else
            {
                p_map_delete(fd, key);
                close(fd);
                prune_pids(name);
            }
        }
    }

    glog(LOG_INFO, "bpf-block %s %s", allow ? "allow" : "deny", name);
    printf("allowed=%d\n", n + ne);
    return 0;
}

/* Allow one running process and what it starts, until it exits (Cardwire's
 * RequestProcessAccess). "force" also hides the iGPU from it. */
static int bpf_block_allow_pid(const char *arg, int force)
{
    char *end = NULL;
    long pid = strtol(arg, &end, 10);
    if (!*arg || *end || pid <= 1 || pid > 4194304)
    {
        fprintf(stderr, "bpf-block: invalid pid\n");
        return 1;
    }
    char path[64], line[128];
    snprintf(path, sizeof(path), "/proc/%ld/status", pid);
    FILE *f = fopen(path, "r");
    if (!f)
    {
        fprintf(stderr, "bpf-block: no such process\n");
        return 1;
    }
    long tgid = pid;
    while (fgets(line, sizeof(line), f))
        if (sscanf(line, "Tgid: %ld", &tgid) == 1)
            break;
    fclose(f);

    /* Same rule as seeding: no record without the hook that removes it. */
    if (access(PIN_DIR "/link_gh_exit", F_OK) != 0 || !load_libbpf())
    {
        fprintf(stderr, "bpf-block: per-process hooks are not attached\n");
        return 4;
    }
    int fd = p_obj_get(PIN_PID);
    if (fd < 0)
        return 3;
    uint32_t key = (uint32_t)tgid;
    uint8_t val = force ? GH_PID_FORCE : GH_PID_ALLOW;
    int rc = p_map_update(fd, &key, &val, 0);
    close(fd);
    if (rc != 0)
        return 3;
    glog(LOG_INFO, "bpf-block allow-pid %ld%s", tgid, force ? " force" : "");
    printf("pid=%ld level=%d\n", tgid, val);
    return 0;
}

/* Must match struct gh_refused in the filter. */
struct gh_refused
{
    uint64_t last_ns;
    uint32_t count;
    uint32_t tgid;
};

struct refused_row
{
    char comm[GH_COMM_LEN];
    struct gh_refused r;
};

static int refused_newest_first(const void *a, const void *b)
{
    uint64_t x = ((const struct refused_row *)a)->r.last_ns;
    uint64_t y = ((const struct refused_row *)b)->r.last_ns;
    if (x == y)
        return 0;
    return x < y ? 1 : -1;
}

/* Processes the filter denied, newest first: "<seconds ago> <count> <name>". */
static int bpf_block_refused(void)
{
    if (access(PIN_REFUSED, F_OK) != 0)
        return 0;
    if (!load_libbpf())
        return 4;
    int fd = p_obj_get(PIN_REFUSED);
    if (fd < 0)
        return 3;

    static struct refused_row rows[256];
    int n = 0;
    char key[GH_COMM_LEN], next[GH_COMM_LEN];
    int have = p_map_next_key(fd, NULL, next) == 0;
    while (have && n < 256)
    {
        memcpy(key, next, sizeof(key));
        if (p_map_lookup(fd, key, &rows[n].r) == 0)
        {
            memcpy(rows[n].comm, key, GH_COMM_LEN);
            rows[n].comm[GH_COMM_LEN - 1] = '\0';
            n++;
        }
        have = p_map_next_key(fd, key, next) == 0;
    }
    close(fd);

    qsort(rows, (size_t)n, sizeof(rows[0]), refused_newest_first);
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    uint64_t now = (uint64_t)ts.tv_sec * 1000000000ull + (uint64_t)ts.tv_nsec;
    for (int i = 0; i < n; i++)
    {
        for (char *c = rows[i].comm; *c; c++)
            if (*c == '\t' || *c == '\n')
                *c = '?';
        uint64_t age = now > rows[i].r.last_ns ? (now - rows[i].r.last_ns) / 1000000000ull : 0;
        printf("%llu\t%u\t%s\n", (unsigned long long)age, rows[i].r.count, rows[i].comm);
    }
    return 0;
}

int do_bpf_block(int argc, char **argv)
{
    if (argc < 3)
    {
        fprintf(stderr, "usage: bpf-block <on|off|status|refresh|allow-comm|deny-comm|list-comms|"
                        "allow-pid|refused|uninstall> [arg]\n");
        return 1;
    }
    const char *verb = argv[2];

    if (strcmp(verb, "off") == 0)
        return bpf_block_off();
    if (strcmp(verb, "status") == 0)
        return bpf_block_status();
    if (strcmp(verb, "list-comms") == 0)
        return bpf_block_list_comms();
    if (strcmp(verb, "refused") == 0)
        return bpf_block_refused();
    if (strcmp(verb, "uninstall") == 0)
        return bpf_block_uninstall();

    if (strcmp(verb, "allow-comm") == 0 || strcmp(verb, "deny-comm") == 0)
    {
        if (argc != 4)
        {
            fprintf(stderr, "usage: bpf-block %s <process-name|KEY=VALUE>\n", verb);
            return 1;
        }
        return bpf_block_set_entry(argv[3], strcmp(verb, "allow-comm") == 0);
    }

    if (strcmp(verb, "allow-pid") == 0)
    {
        if (argc < 4 || argc > 5 || (argc == 5 && strcmp(argv[4], "force") != 0))
        {
            fprintf(stderr, "usage: bpf-block allow-pid <pid> [force]\n");
            return 1;
        }
        return bpf_block_allow_pid(argv[3], argc == 5);
    }

    if (strcmp(verb, "on") == 0 || strcmp(verb, "refresh") == 0)
    {
        if (argc != 4)
        {
            fprintf(stderr, "usage: bpf-block %s <bdf>\n", verb);
            return 1;
        }
        if (!valid_bdf(argv[3]))
        {
            fprintf(stderr, "bpf-block: invalid bdf\n");
            return 1;
        }
        return strcmp(verb, "on") == 0 ? bpf_block_on(argv[3]) : bpf_block_refresh(argv[3]);
    }

    fprintf(stderr, "bpf-block: verb not permitted\n");
    return 1;
}
