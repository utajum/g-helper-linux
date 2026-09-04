/* Shared declarations for gpu-helper modules. */
#ifndef GPU_HELPER_H
#define GPU_HELPER_H

#include <ctype.h>
#include <dirent.h>
#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <pwd.h>
#include <signal.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <syslog.h>
#include <unistd.h>

#define NVIDIA_PREFIX "/dev/nvidia"
#define NVIDIA_PREFIX_LEN 11
#define COMM_BUF_SIZE 64
#define PATH_BUF_SIZE 512
#define MAPS_LINE_SIZE 1024
#define CGROUP_BUF_SIZE 1024
#define UNIT_BUF_SIZE 256
#define MAX_STOPPED_UNITS 64

/* gpu-helper.c: shared utilities */
void glog(int prio, const char *fmt, ...);
int exec_tool(const char *tool, char *const argv[]);
void run_cmd(char *const argv[]);

/* pci_ops.c: also used by nvidia_ops.c (drm-notify-remove) */
int valid_bdf(const char *s);

/* process_ops.c */
int do_list(int skip_pid);
int do_kill(int argc, char **argv);

/* nvidia_ops.c */
int do_daemon(int argc, char **argv);
int do_rmmod(int argc, char **argv);
int do_smi(int argc, char **argv);
int do_modprobe(int argc, char **argv);
int do_vulkan_icd(int argc, char **argv);
int do_egl_vendor(int argc, char **argv);
int do_drm_notify_remove(int argc, char **argv);
int do_nvidia_refcnt_holders(void);
int do_nvml(int argc, char **argv);
int do_nvml_temp(void);
int do_nvml_info(void);
int do_nvml_procs(void);

/* pci_ops.c */
int do_pci(const char *action, int argc, char **argv);
int do_pci_remove(int argc, char **argv);
int do_slot_power(int argc, char **argv);
int do_pci_power(int argc, char **argv);

/* wmi_ops.c */
int do_wmi_dsts(int argc, char **argv);
int do_wmi_devs(int argc, char **argv);
int do_wmi_probe(void);

/* ec_ops.c */
int do_ec_fanctl(void);

/* msr_ops.c */
int do_msr_uv(int argc, char **argv);

/* lenovo_ops.c */
int do_lenovo_flip_to_start(int argc, char **argv);

#endif
