/*
 * Minimal BPF program-side definitions for the GPU block filter.
 *
 * libbpf ships bpf_helpers.h / bpf_core_read.h / bpf_tracing.h for this, but
 * pulling them in means a libbpf-dev build dependency, and vendoring them means
 * ~2500 lines of third party header in the tree. The filter needs three helpers
 * and a handful of macros, so they are declared here instead. Build dependency
 * stays at clang alone.
 *
 * Helper function ids are the stable BPF ABI from include/uapi/linux/bpf.h and
 * never change.
 */
#ifndef GHELPER_BPF_MIN_H
#define GHELPER_BPF_MIN_H

/* For the __uN types used in the helper signatures below. Included here rather
 * than assumed from the includer, because clang-format sorts include lines
 * alphabetically and would otherwise put this header first and break the
 * build. */
#include "vmlinux_min.h"

#define SEC(name) __attribute__((section(name), used))

/* The verifier has no call frame for these, they must be inlined. */
#define __always_inline inline __attribute__((always_inline))

/* Map type constants from uapi/linux/bpf.h. */
#define BPF_MAP_TYPE_ARRAY 2
#define BPF_MAP_TYPE_HASH 1
#define BPF_MAP_TYPE_LRU_HASH 9
#define BPF_MAP_TYPE_PERCPU_ARRAY 6

/* BTF-defined map fields. The kernel reads these from the object's BTF, the
 * declared array sizes carry the values. */
#define __uint(name, val) int(*name)[val]
#define __type(name, val) typeof(val) *name

/* Helper ids: see uapi/linux/bpf.h enum bpf_func_id. */
static void *(*bpf_map_lookup_elem)(void *map, const void *key) = (void *)1;
static long (*bpf_map_update_elem)(void *map, const void *key, const void *value, __u64 flags) = (void *)2;
static long (*bpf_map_delete_elem)(void *map, const void *key) = (void *)3;
static __u64 (*bpf_ktime_get_ns)(void) = (void *)5;
static __u64 (*bpf_get_current_pid_tgid)(void) = (void *)14;
static long (*bpf_get_current_comm)(void *buf, __u32 size) = (void *)16;
static long (*bpf_probe_write_user)(void *dst, const void *src, __u32 size) = (void *)36;
static __u64 (*bpf_get_current_task)(void) = (void *)35;
static long (*bpf_probe_read_user)(void *dst, __u32 size, const void *src) = (void *)112;
static long (*bpf_probe_read_kernel)(void *dst, __u32 size, const void *src) = (void *)113;
static long (*bpf_probe_read_user_str)(void *dst, __u32 size, const void *src) = (void *)114;
static long (*bpf_loop)(__u32 nr_loops, void *callback_fn, void *callback_ctx, __u64 flags) = (void *)181;

/*
 * Read one field out of a kernel struct.
 *
 * __builtin_preserve_access_index makes clang emit a CO-RE relocation for the
 * field offset, which libbpf patches to the running kernel's layout at load
 * time. That is what lets one compiled object work across kernel versions
 * while vmlinux_min.h declares only the fields we touch.
 *
 * bpf_probe_read_kernel is used rather than a direct dereference. LSM
 * arguments are trusted BTF pointers so a direct load would verify on current
 * kernels, but probe_read carries no trusted-pointer requirement and so loads
 * on the widest range of kernels.
 */
#define GH_READ(dst, src, field) \
    bpf_probe_read_kernel(&(dst), sizeof(dst), __builtin_preserve_access_index(&(src)->field))

#endif
