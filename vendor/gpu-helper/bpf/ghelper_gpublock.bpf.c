/* ghelper_gpublock: eBPF LSM filter that returns ENOENT for dGPU nodes, keyed by (dev, ino). */

#include "bpf_min.h"
#include "vmlinux_min.h"

char LICENSE[] SEC("license") = "GPL";

/* ENOENT: apps treat the file as absent and fall back to another GPU. */
#define GH_ENOENT (-2)
#define GH_ALLOW 0
#define GH_COMM_LEN 16

/* Longest entry name we key on; longer names never match. */
#define GH_NAME_LEN 32

/* Largest getdents64 result the splice pass walks. */
#define GH_DIRENT_BUF_MAX 32768

/* Splice pass bound: one record per header size, plus the end. */
#define GH_DIRENT_MAX ((GH_DIRENT_BUF_MAX / 24) + 1)

/* sysfs has one superblock per netns, so its entries use a sentinel dev; kernfs inodes are shared. */
#define GH_SYSFS_MAGIC 0x62656572
#define GH_SYSFS_DEV 0xffffffff

struct gh_file_key
{
    __u32 dev;
    __u32 pad;
    __u64 ino;
};

/* 0 = filter inactive (pass everything), 1 = blocking. Kept as a map rather
 * than attach/detach so toggling never has to reload the program. */
struct
{
    __uint(type, BPF_MAP_TYPE_ARRAY);
    __uint(max_entries, 1);
    __type(key, __u32);
    __type(value, __u8);
} gh_mode SEC(".maps");

/* Files belonging to the blocked GPU (GH_INO_DGPU), plus the iGPU's device
 * nodes (GH_INO_IGPU), which only forced processes are denied. */
#define GH_INO_DGPU 1
#define GH_INO_IGPU 2

struct
{
    __uint(type, BPF_MAP_TYPE_HASH);
    __uint(max_entries, 4096);
    __type(key, struct gh_file_key);
    __type(value, __u8);
} gh_blocked_ino SEC(".maps");

/* Built-in names that keep access: udev, package managers and ghelper. */
#define GH_COMM_USER 2 /* user pick: also passes access to the programs it starts */

struct
{
    __uint(type, BPF_MAP_TYPE_HASH);
    __uint(max_entries, 64);
    __type(key, char[GH_COMM_LEN]);
    __type(value, __u8);
} gh_allowed_comm SEC(".maps");

/* tgids allowed by ancestry, environment or request, set at exec and fork; LRU so a missed
 * exit cannot fill it. GH_PID_FORCE also hides the iGPU from the process. */
#define GH_PID_ALLOW 1
#define GH_PID_FORCE 2

struct
{
    __uint(type, BPF_MAP_TYPE_LRU_HASH);
    __uint(max_entries, 8192);
    __type(key, __u32);
    __type(value, __u8);
} gh_allowed_pid SEC(".maps");

/* Environment entries that allow a process at exec ("__NV_PRIME_RENDER_OFFLOAD=1",
 * "SteamAppId=570"); the value is a GH_PID_ level. Longer entries never match. */
#define GH_ENV_LEN 48

struct
{
    __uint(type, BPF_MAP_TYPE_HASH);
    __uint(max_entries, 128);
    __type(key, char[GH_ENV_LEN]);
    __type(value, __u8);
} gh_allowed_env SEC(".maps");

/* Scratch for reading environment strings; too big for the stack. */
#define GH_ENV_READ 512
#define GH_ENV_MAX 256

struct gh_env_buf
{
    char s[GH_ENV_READ];
};

struct
{
    __uint(type, BPF_MAP_TYPE_PERCPU_ARRAY);
    __uint(max_entries, 1);
    __type(key, __u32);
    __type(value, struct gh_env_buf);
} gh_env_scratch SEC(".maps");

/* Process names that were denied, for the apps window. */
struct gh_refused
{
    __u64 last_ns;
    __u32 count;
    __u32 tgid;
};

struct
{
    __uint(type, BPF_MAP_TYPE_LRU_HASH);
    __uint(max_entries, 256);
    __type(key, char[GH_COMM_LEN]);
    __type(value, struct gh_refused);
} gh_refused SEC(".maps");

/* Entry names spliced out of getdents64, so code that lists then reads never sees the device. */
struct gh_dir_key
{
    char name[GH_NAME_LEN];
};

struct
{
    __uint(type, BPF_MAP_TYPE_HASH);
    __uint(max_entries, 512);
    __type(key, struct gh_dir_key);
    __type(value, __u8);
} gh_hidden_dirent SEC(".maps");

/* getdents64 buffer address, carried from the syscall entry to its exit.
 * Keyed by thread id: the buffer belongs to the calling thread only. */
struct
{
    __uint(type, BPF_MAP_TYPE_HASH);
    __uint(max_entries, 1024);
    __type(key, __u32);
    __type(value, __u64);
} gh_getdents SEC(".maps");

static __always_inline int gh_mode_active(void)
{
    __u32 zero = 0;
    __u8 *mode = bpf_map_lookup_elem(&gh_mode, &zero);
    return mode && *mode == 1;
}

/* Process name of a task (its thread group leader's comm), as a map key. */
static __always_inline int gh_leader_comm(struct task_struct *task, char comm[GH_COMM_LEN])
{
    struct task_struct *leader = 0;
    if (!task || GH_READ(leader, task, group_leader) != 0 || !leader)
        return 0;
    if (bpf_probe_read_kernel(comm, GH_COMM_LEN, __builtin_preserve_access_index(&leader->comm)) != 0)
        return 0;

    /* The map key is exact bytes and old kernels do not pad comm after the
     * terminator, so zero the tail before the lookup. */
    int seen_nul = 0;
    for (int i = 0; i < GH_COMM_LEN; i++)
    {
        if (seen_nul)
            comm[i] = 0;
        else if (comm[i] == 0)
            seen_nul = 1;
    }
    return 1;
}

/* Level recorded for the caller's process: 0, GH_PID_ALLOW or GH_PID_FORCE. */
static __always_inline int gh_pid_level(void)
{
    __u32 tgid = bpf_get_current_pid_tgid() >> 32;
    __u8 *level = bpf_map_lookup_elem(&gh_allowed_pid, &tgid);
    return level ? *level : 0;
}

/* Access level of the caller: its pid record, else GH_PID_ALLOW by name. */
static __always_inline int gh_caller(void)
{
    int level = gh_pid_level();
    if (level)
        return level;

    char comm[GH_COMM_LEN] = {};
    if (bpf_get_current_comm(&comm, sizeof(comm)) != 0)
        return GH_PID_ALLOW; /* cannot identify the caller, do not block it */
    if (bpf_map_lookup_elem(&gh_allowed_comm, &comm))
        return GH_PID_ALLOW;

    /* Match the thread name and the process name: threads rename themselves. */
    char lcomm[GH_COMM_LEN] = {};
    if (!gh_leader_comm((struct task_struct *)bpf_get_current_task(), lcomm))
        return 0;
    return bpf_map_lookup_elem(&gh_allowed_comm, &lcomm) ? GH_PID_ALLOW : 0;
}

/* Count a denial under the caller's process name. */
static __always_inline void gh_note_refused(void)
{
    char comm[GH_COMM_LEN] = {};
    if (!gh_leader_comm((struct task_struct *)bpf_get_current_task(), comm))
        return;
    __u32 tgid = bpf_get_current_pid_tgid() >> 32;
    struct gh_refused *r = bpf_map_lookup_elem(&gh_refused, &comm);
    if (r)
    {
        r->last_ns = bpf_ktime_get_ns();
        r->count++;
        r->tgid = tgid;
        return;
    }
    struct gh_refused fresh = {bpf_ktime_get_ns(), 1, tgid};
    bpf_map_update_elem(&gh_refused, &comm, &fresh, 0);
}

/* Look up an inode's entry: GH_INO_DGPU, GH_INO_IGPU or 0. */
static __always_inline int gh_inode_kind(struct inode *inode)
{
    if (!inode)
        return 0;

    struct super_block *sb = 0;
    if (GH_READ(sb, inode, i_sb) != 0 || !sb)
        return 0;

    struct gh_file_key key = {};
    unsigned long magic = 0;
    if (GH_READ(magic, sb, s_magic) != 0)
        return 0;
    if (magic == GH_SYSFS_MAGIC)
        key.dev = GH_SYSFS_DEV;
    else if (GH_READ(key.dev, sb, s_dev) != 0)
        return 0;
    if (GH_READ(key.ino, inode, i_ino) != 0)
        return 0;

    __u8 *kind = bpf_map_lookup_elem(&gh_blocked_ino, &key);
    return kind ? *kind : 0;
}

/* The decision for one file, after the mode check. */
static __always_inline int gh_decide(struct inode *inode)
{
    int kind = gh_inode_kind(inode);
    if (!kind)
        return GH_ALLOW;
    if (kind == GH_INO_IGPU)
        return gh_pid_level() == GH_PID_FORCE ? GH_ENOENT : GH_ALLOW;
    if (gh_caller())
        return GH_ALLOW;
    gh_note_refused();
    return GH_ENOENT;
}

static __always_inline int gh_check(struct inode *inode)
{
    if (!gh_mode_active())
        return GH_ALLOW;
    return gh_decide(inode);
}

/* LSM program arguments arrive as an array of u64. */

/* Primary hook. Every open of a blocked device node or sysfs file fails here,
 * which is what stops an app from powering the GPU up. */
SEC("lsm/file_open")
int gh_file_open(unsigned long long *ctx)
{
    struct file *file = (struct file *)ctx[0];
    if (!file)
        return GH_ALLOW;

    /* Cheap guard before touching the file, so an inactive filter costs one
     * array lookup on a path that runs for every open on the system. */
    if (!gh_mode_active())
        return GH_ALLOW;

    struct inode *inode = 0;
    if (GH_READ(inode, file, f_inode) != 0)
        return GH_ALLOW;

    return gh_decide(inode);
}

/* Fires per path component, so a blocked directory covers lazy files under it. */
SEC("lsm/inode_permission")
int gh_inode_permission(unsigned long long *ctx)
{
    return gh_check((struct inode *)ctx[0]);
}

/* stat() and friends, so a probe cannot even see the node. */
SEC("lsm/inode_getattr")
int gh_inode_getattr(unsigned long long *ctx)
{
    struct path *path = (struct path *)ctx[0];
    if (!path)
        return GH_ALLOW;

    if (!gh_mode_active())
        return GH_ALLOW;

    struct dentry *dentry = 0;
    if (GH_READ(dentry, path, dentry) != 0 || !dentry)
        return GH_ALLOW;

    struct inode *inode = 0;
    if (GH_READ(inode, dentry, d_inode) != 0)
        return GH_ALLOW;

    return gh_decide(inode);
}

/* ---------- directory listing: hide an entry by adding its d_reclen to the previous one ---------- */

/* Save the buffer and directory inode at entry; the exit tracepoint has no args. */
SEC("tracepoint/syscalls/sys_enter_getdents64")
int gh_getdents_enter(void *ctx)
{
    if (!gh_mode_active())
        return 0;
    if (gh_caller())
        return 0;

    /* args[0] = fd, args[1] = dirent buffer. */
    __u64 buf = 0;
    if (bpf_probe_read_kernel(&buf, sizeof(buf), (char *)ctx + 24) != 0 || !buf)
        return 0;

    __u32 tid = (__u32)bpf_get_current_pid_tgid();
    bpf_map_update_elem(&gh_getdents, &tid, &buf, 0);
    return 0;
}

/* Splice state for one getdents64 result, shared across bpf_loop steps. */
struct gh_dirent_walk
{
    __u64 cur;
    __u64 end;
    __u64 prev;
    __u16 prev_reclen;
};

/* One record per bpf_loop step: verified once, whatever the compiler emits. 1 stops the walk. */
static long gh_dirent_step(__u64 index, void *ctx)
{
    struct gh_dirent_walk *w = ctx;
    (void)index;

    /* Bounds check before every read: the loop bound alone does not
     * prove we stayed inside the caller's buffer. */
    if (w->cur + sizeof(struct gh_dirent64) > w->end)
        return 1;

    struct gh_dirent64 rec = {};
    if (bpf_probe_read_user(&rec, sizeof(rec), (void *)w->cur) != 0)
        return 1;

    __u16 reclen = rec.d_reclen;
    /* A record shorter than its own header cannot be valid, and advancing
     * by it would not terminate. */
    if (reclen < sizeof(struct gh_dirent64) || w->cur + reclen > w->end)
        return 1;

    struct gh_dir_key key = {};
    if (bpf_probe_read_user_str(&key.name, sizeof(key.name),
                                (void *)(w->cur + __builtin_offsetof(struct gh_dirent64, d_name))) < 0)
        return 1;

    __u8 *hidden = bpf_map_lookup_elem(&gh_hidden_dirent, &key);

    if (hidden && w->prev)
    {
        /* Fold this record into the previous one. The caller walks by
         * d_reclen, so it never sees this entry. */
        __u16 merged = w->prev_reclen + reclen;
        if (bpf_probe_write_user((void *)(w->prev + __builtin_offsetof(struct gh_dirent64, d_reclen)),
                                 &merged, sizeof(merged)) != 0)
            return 1;
        w->prev_reclen = merged;
    }
    else
    {
        w->prev = w->cur;
        w->prev_reclen = reclen;
    }

    w->cur += reclen;
    return 0;
}

/* Walk the returned records and splice out the hidden ones. */
SEC("tracepoint/syscalls/sys_exit_getdents64")
int gh_getdents_exit(void *ctx)
{
    __u32 tid = (__u32)bpf_get_current_pid_tgid();

    __u64 *slot = bpf_map_lookup_elem(&gh_getdents, &tid);
    if (!slot)
        return 0;

    __u64 buf = *slot;
    bpf_map_delete_elem(&gh_getdents, &tid);
    if (!buf)
        return 0;

    /* ret is at offset 16 in the exit tracepoint format. Negative means the
     * syscall failed, zero means end of directory. */
    __s64 ret = 0;
    if (bpf_probe_read_kernel(&ret, sizeof(ret), (char *)ctx + 16) != 0)
        return 0;
    if (ret <= 0 || ret > GH_DIRENT_BUF_MAX)
        return 0;

    struct gh_dirent_walk w = {.cur = buf, .end = buf + (__u64)ret};
    bpf_loop(GH_DIRENT_MAX, gh_dirent_step, &w, 0);
    return 0;
}

/* Per-process decisions, optional: without them allowed apps pass by name only. */

/* Is this task's process a user pick, by its main thread's name. */
static __always_inline int gh_user_comm(struct task_struct *task)
{
    char comm[GH_COMM_LEN] = {};
    if (!gh_leader_comm(task, comm))
        return 0;
    __u8 *val = bpf_map_lookup_elem(&gh_allowed_comm, &comm);
    return val && *val == GH_COMM_USER;
}

/* GH_PID_ bits the environment entries in [cur, end) ask for. Global: verified once. */
__attribute__((noinline)) int gh_env_level(__u64 cur, __u64 end)
{
    __u32 zero = 0;
    struct gh_env_buf *buf = bpf_map_lookup_elem(&gh_env_scratch, &zero);
    if (!buf)
        return 0;

    int bits = 0;
    for (int i = 0; i < GH_ENV_MAX && cur < end; i++)
    {
        __builtin_memset(buf->s, 0, GH_ENV_LEN);
        long n = bpf_probe_read_user_str(buf->s, sizeof(buf->s), (void *)cur);
        if (n <= 0)
            break;
        __u8 *val = bpf_map_lookup_elem(&gh_allowed_env, buf->s);
        if (val)
            bits |= *val;
        cur += n - n / GH_ENV_READ;
    }
    return bits;
}

/* New image: keeps its own or its parent's level, and gains one for a user
 * pick name or an allowing environment entry. */
SEC("raw_tracepoint/sched_process_exec")
int gh_exec(void *ctx)
{
    if (!gh_mode_active())
        return 0;

    __u32 tgid = bpf_get_current_pid_tgid() >> 32;
    struct task_struct *task = (struct task_struct *)bpf_get_current_task();
    struct task_struct *parent = 0;
    int ptgid = 0;
    if (task && GH_READ(parent, task, real_parent) == 0 && parent)
        GH_READ(ptgid, parent, tgid);
    __u32 pkey = (__u32)ptgid;

    int level = 0;
    __u8 *own = bpf_map_lookup_elem(&gh_allowed_pid, &tgid);
    if (own)
        level = *own;
    __u8 *from = ptgid > 0 ? bpf_map_lookup_elem(&gh_allowed_pid, &pkey) : 0;
    if (from && *from > level)
        level = *from;
    if (!level && (gh_user_comm(task) || gh_user_comm(parent)))
        level = GH_PID_ALLOW;
    struct mm_struct *mm = 0;
    __u64 env_start = 0, env_end = 0;
    if (level < GH_PID_FORCE && task && GH_READ(mm, task, mm) == 0 && mm &&
        GH_READ(env_start, mm, env_start) == 0 && GH_READ(env_end, mm, env_end) == 0)
    {
        int env = gh_env_level(env_start, env_end);
        if (env & GH_PID_FORCE)
            level = GH_PID_FORCE;
        else if (env)
            level = GH_PID_ALLOW;
    }

    __u8 val = (__u8)level;
    if (val)
        bpf_map_update_elem(&gh_allowed_pid, &tgid, &val, 0);
    return 0;
}

/* A forked child inherits the parent's level; new threads share the tgid. */
SEC("raw_tracepoint/sched_process_fork")
int gh_fork(unsigned long long *ctx)
{
    if (!gh_mode_active())
        return 0;

    __u32 ptgid = bpf_get_current_pid_tgid() >> 32;
    __u8 *level = bpf_map_lookup_elem(&gh_allowed_pid, &ptgid);
    if (!level)
        return 0;

    struct task_struct *child = (struct task_struct *)ctx[1];
    int pid = 0, tgid = 0;
    if (!child || GH_READ(pid, child, pid) != 0 || GH_READ(tgid, child, tgid) != 0)
        return 0;
    if (pid != tgid)
        return 0;

    __u32 key = (__u32)tgid;
    __u8 val = *level;
    bpf_map_update_elem(&gh_allowed_pid, &key, &val, 0);
    return 0;
}

/* Drop the record when the process ends, so a reused pid starts clean. */
SEC("raw_tracepoint/sched_process_exit")
int gh_exit(void *ctx)
{
    __u64 id = bpf_get_current_pid_tgid();
    __u32 tgid = id >> 32;
    if ((__u32)id != tgid)
        return 0;
    bpf_map_delete_elem(&gh_allowed_pid, &tgid);
    return 0;
}
