/*
 * Minimal kernel type declarations for the GPU block filter.
 *
 * Only the fields the filter walks are declared. Every struct carries
 * preserve_access_index so libbpf relocates the field offsets against the
 * running kernel's BTF at load time, which is what makes one compiled object
 * work across kernel versions.
 *
 * The alternative is a generated vmlinux.h from bpftool, which is ~100k lines
 * and would have to be regenerated and reviewed on every kernel bump. Declaring
 * the structs we touch is smaller and easier to audit.
 *
 * CO-RE resolves a field by name and walks into anonymous struct/union members,
 * so declaring f_path directly still matches kernels that wrap it in an
 * anonymous union.
 */
#ifndef GHELPER_VMLINUX_MIN_H
#define GHELPER_VMLINUX_MIN_H

typedef unsigned char __u8;
typedef unsigned short __u16;
typedef unsigned int __u32;
typedef unsigned long long __u64;
typedef int __s32;
typedef long long __s64;

#pragma clang attribute push(__attribute__((preserve_access_index)), apply_to = record)

struct super_block
{
    __u32 s_dev;
    unsigned long s_magic;
};

struct inode
{
    __u64 i_ino;
    struct super_block *i_sb;
};

struct qstr
{
    __u32 hash;
    __u32 len;
    const unsigned char *name;
};

struct dentry
{
    struct qstr d_name;
    struct inode *d_inode;
};

struct vfsmount;

struct path
{
    struct vfsmount *mnt;
    struct dentry *dentry;
};

struct file
{
    struct path f_path;
    struct inode *f_inode;
};

struct mm_struct
{
    unsigned long env_start;
    unsigned long env_end;
};

/* comm is TASK_COMM_LEN (16) in every kernel this can load on. */
struct task_struct
{
    int pid;
    int tgid;
    struct task_struct *real_parent;
    struct task_struct *group_leader;
    struct mm_struct *mm;
    char comm[16];
};

#pragma clang attribute pop

/* getdents64 record layout, from uapi/linux/dirent.h. This one is a userspace
 * ABI struct, not a kernel-internal type, so it carries no CO-RE relocation:
 * the layout is fixed and identical on every kernel. */
struct gh_dirent64
{
    __u64 d_ino;
    __s64 d_off;
    __u16 d_reclen;
    unsigned char d_type;
    char d_name[];
};

#endif
