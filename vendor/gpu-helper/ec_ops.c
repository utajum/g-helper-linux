/* ASUS EC HealthyTable fan control for gpu-helper.
 *
 * Same EC commands as the MyASUS fan test (AsusWinIo64.dll HealthyTable_*):
 * 0xBB <id> reads one info byte, 0xDD <dir> <reg> <value> reads or writes
 * the HealthyTable. Two links carry them; the first that passes detection
 * is used:
 *   lpc  private port pair 0x25C data / 0x25D command+status (AsusSAIO.sys,
 *        and on 2026+ x64 models the ASUS EC driver the AsusEC DLL loads)
 *   hid  feature report 7 of HID 0B05:4543, usage page 0xFF32 (AsusEC DLL)
 * Detection is the MyASUS rule: table version (0xBB 0x50) >= 2, and below
 * version 5 the vendor bit (0xBB 0x51 bit 7) must say ASUS, not ODM. */
#include "gpu-helper.h"
#include <linux/hidraw.h>
#include <poll.h>
#include <sys/file.h>
#include <sys/ioctl.h>
#include <time.h>

#if defined(__x86_64__) || defined(__i386__)
#include <sys/io.h>
#define EC_HAVE_IO 1
#endif

/* The test build (-DGH_EC_SIM=dir) swaps port I/O and hidraw for a simulated
 * EC (install/tests/ec-sim.h) and keeps its files in dir. */
#ifdef GH_EC_SIM
#include "ec-sim.h"
#else
#define EC_LOCK "/run/ghelper-ec-fan.lock"
#define EC_DMI "/sys/firmware/dmi/tables/DMI"
#define EC_HIDRAW_DIR "/sys/class/hidraw"
#define EC_DEV_DIR "/dev"
#define EC_HID_GAP_US 210000 /* the AsusEC DLL waits this long between batched commands */
#endif

#define EC_DATA 0x25C
#define EC_CMD 0x25D
#define EC_OBF 0x01
#define EC_IBF 0x02

#define EC_PREFIX 0xFF
#define EC_CMD_INFO 0xBB  /* payload id, replies one byte */
#define EC_CMD_TABLE 0xDD /* payload dir, reg, value */
#define EC_INFO_VERSION 0x50
#define EC_INFO_VENDOR 0x51 /* bit 7: 1 = ASUS, 0 = ODM */
#define EC_READ 0x02
#define EC_WRITE 0x82

#define EC_REG_FAN_COUNT 0x30
#define EC_REG_MODE 0x31 /* fan test mode: 0 = BIOS curve, 1 = manual; one bit for all fans */
#define EC_REG_FAN_SEL 0x32
#define EC_REG_RPM_LO 0x33
#define EC_REG_RPM_HI 0x34
#define EC_REG_DUTY 0x35

#define EC_POLL_LIMIT 1000
#define EC_POLL_US 100
#define EC_GAP_US 20000 /* lpc: EC needs settle time after each transaction */
#define EC_MAX_FANS 3
#define EC_WATCHDOG_S 5 /* give the fans back after this long without a command */

#define EC_HID_VID 0x0B05
#define EC_HID_PID 0x4543
#define EC_HID_REPORT 7
#define EC_HID_LEN 64
#define EC_HID_OK 0x80

struct ec_link
{
    const char *name;
    int (*open)(void);
    int (*xfer)(int cmd, const unsigned char *data, int len, unsigned char *reply);
    void (*close)(void);
};

static const struct ec_link *ec;
static volatile sig_atomic_t ec_stop;

static void ec_on_signal(int sig)
{
    (void)sig;
    ec_stop = 1;
}

static void ec_sleep_us(long us)
{
    struct timespec ts = {us / 1000000, (us % 1000000) * 1000};
    nanosleep(&ts, NULL);
}

/* Reads until EOF: sysfs hands out binary files one page per read. */
static int read_file(const char *path, unsigned char *buf, int size)
{
    int fd = open(path, O_RDONLY | O_CLOEXEC);
    if (fd < 0)
        return -1;
    int n = 0, r;
    while (n < size && (r = (int)read(fd, buf + n, size - n)) > 0)
        n += r;
    close(fd);
    return n;
}

static int contains(const unsigned char *buf, int n, const unsigned char *pat, int m)
{
    for (int i = 0; i + m <= n; i++)
        if (memcmp(buf + i, pat, m) == 0)
            return 1;
    return 0;
}

#ifdef EC_HAVE_IO

/* Poll status until (status & mask) == want. */
static int lpc_wait(int mask, int want)
{
    for (int i = 0; i < EC_POLL_LIMIT; i++)
    {
        if ((inb(EC_CMD) & mask) == want)
            return 0;
        ec_sleep_us(EC_POLL_US);
    }
    return -1;
}

static void lpc_drain(void)
{
    for (int i = 0; i < EC_POLL_LIMIT && (inb(EC_CMD) & EC_OBF); i++)
    {
        (void)inb(EC_DATA);
        ec_sleep_us(EC_POLL_US);
    }
}

/* Claim the ports only when the SMBIOS tables name ASUSTeK: the 2026+ ASUS
 * EC driver makes this check, and its DLL stops when it fails. */
static int lpc_open(void)
{
    static unsigned char dmi[65536];
    int n = read_file(EC_DMI, dmi, sizeof(dmi));
    if (!contains(dmi, n, (const unsigned char *)"ASUSTeK Computer Inc", 20) &&
        !contains(dmi, n, (const unsigned char *)"ASUSTeK COMPUTER INC", 20))
        return -1;
    return ioperm(EC_DATA, 2, 1);
}

/* One transaction: prefix, command, payload bytes, optional one-byte reply. */
static int lpc_xfer(int cmd, const unsigned char *data, int len, unsigned char *reply)
{
    lpc_drain();
    if (lpc_wait(EC_IBF, 0) != 0)
        return -1;
    outb(EC_PREFIX, EC_CMD);
    if (lpc_wait(EC_IBF, 0) != 0)
        return -1;
    outb((unsigned char)cmd, EC_CMD);
    for (int i = 0; i < len; i++)
    {
        if (lpc_wait(EC_IBF, 0) != 0)
            return -1;
        outb(data[i], EC_DATA);
    }
    if (lpc_wait(EC_IBF, 0) != 0)
        return -1;
    if (reply != NULL)
    {
        if (lpc_wait(EC_OBF, EC_OBF) != 0)
            return -1;
        *reply = inb(EC_DATA);
    }
    ec_sleep_us(EC_GAP_US);
    return 0;
}

static void lpc_close(void)
{
    ioperm(EC_DATA, 2, 0);
}

static const struct ec_link lpc_link = {"lpc", lpc_open, lpc_xfer, lpc_close};

#endif /* EC_HAVE_IO */

static int hid_fd = -1;

/* The hidraw node of 0B05:4543 whose report descriptor declares usage
 * page 0xFF32, usage 0x84: the EC mailbox the AsusEC DLL opens. */
static int hid_open(void)
{
    static const unsigned char usage[] = {0x06, 0x32, 0xFF, 0x09, 0x84};
    DIR *dir = opendir(EC_HIDRAW_DIR);
    if (dir == NULL)
        return -1;
    struct dirent *e;
    while (hid_fd < 0 && (e = readdir(dir)) != NULL)
    {
        char path[PATH_BUF_SIZE];
        unsigned char buf[4096];
        unsigned int bus, vid, pid;
        if (strncmp(e->d_name, "hidraw", 6) != 0)
            continue;
        snprintf(path, sizeof(path), EC_HIDRAW_DIR "/%s/device/uevent", e->d_name);
        int n = read_file(path, buf, sizeof(buf) - 1);
        if (n <= 0)
            continue;
        buf[n] = 0;
        char *id = strstr((char *)buf, "HID_ID=");
        if (id == NULL || sscanf(id, "HID_ID=%x:%x:%x", &bus, &vid, &pid) != 3 ||
            vid != EC_HID_VID || pid != EC_HID_PID)
            continue;
        snprintf(path, sizeof(path), EC_HIDRAW_DIR "/%s/device/report_descriptor", e->d_name);
        n = read_file(path, buf, sizeof(buf));
        if (n <= 0 || !contains(buf, n, usage, sizeof(usage)))
            continue;
        snprintf(path, sizeof(path), EC_DEV_DIR "/%s", e->d_name);
        hid_fd = open(path, O_RDWR | O_CLOEXEC);
    }
    closedir(dir);
    return hid_fd >= 0 ? 0 : -1;
}

/* Request: [7, cmd, reply length, payload]. Reply (get feature):
 * [7, cmd, length, status 0x80 = ok, data]. Writes get no reply. */
static int hid_xfer(int cmd, const unsigned char *data, int len, unsigned char *reply)
{
    unsigned char buf[EC_HID_LEN] = {EC_HID_REPORT, (unsigned char)cmd, reply != NULL};
    int rc = -1;
    if (len <= EC_HID_LEN - 3)
    {
        memcpy(buf + 3, data, len);
        rc = ioctl(hid_fd, HIDIOCSFEATURE(EC_HID_LEN), buf) < 0 ? -1 : 0;
    }
    if (rc == 0 && reply != NULL)
    {
        memset(buf, 0, sizeof(buf));
        buf[0] = EC_HID_REPORT;
        if (ioctl(hid_fd, HIDIOCGFEATURE(EC_HID_LEN), buf) < 0 || buf[3] != EC_HID_OK || buf[2] < 1)
            rc = -1;
        else
            *reply = buf[4];
    }
    ec_sleep_us(EC_HID_GAP_US);
    return rc;
}

static void hid_close(void)
{
    close(hid_fd);
    hid_fd = -1;
}

static const struct ec_link hid_link = {"hid", hid_open, hid_xfer, hid_close};

static const struct ec_link *const ec_links[] = {
#ifdef EC_HAVE_IO
    &lpc_link,
#endif
    &hid_link,
};

static int ht_info(int id, unsigned char *value)
{
    const unsigned char d[] = {(unsigned char)id};
    return ec->xfer(EC_CMD_INFO, d, 1, value);
}

static int ht_read(int reg, unsigned char *value)
{
    const unsigned char d[] = {EC_READ, (unsigned char)reg, 0};
    return ec->xfer(EC_CMD_TABLE, d, 3, value);
}

static int ht_write(int reg, int value)
{
    const unsigned char d[] = {EC_WRITE, (unsigned char)reg, (unsigned char)value};
    return ec->xfer(EC_CMD_TABLE, d, 3, NULL);
}

static int ht_rpm(int fan, int *rpm)
{
    unsigned char lo, hi;
    if (ht_write(EC_REG_FAN_SEL, fan) != 0 || ht_read(EC_REG_RPM_HI, &hi) != 0 ||
        ht_read(EC_REG_RPM_LO, &lo) != 0)
        return -1;
    *rpm = (hi << 8) | lo;
    return 0;
}

/* MyASUS fan test rule. NULL when the EC qualifies, else the reason. */
static const char *ht_detect(int *version, int *fans)
{
    unsigned char ver, vendor, count;
    if (ht_info(EC_INFO_VERSION, &ver) != 0 || ver == 0xFF)
        return "silent";
    if (ver < 2)
        return "version";
    if (ver < 5 && (ht_info(EC_INFO_VENDOR, &vendor) != 0 || !(vendor & 0x80)))
        return "odm";
    if (ht_read(EC_REG_FAN_COUNT, &count) != 0 || count < 1 || count > EC_MAX_FANS)
        return "fans";
    *version = ver;
    *fans = count;
    return NULL;
}

/* "probe ok <link> <version> <fans> <rpm>..." or "probe no <reason>".
 * Takes the lock first so two controllers never share the EC. */
static int ec_probe(int *lock_fd, int *fans)
{
    *lock_fd = open(EC_LOCK, O_CREAT | O_RDWR | O_CLOEXEC | O_NOFOLLOW, 0600);
    if (*lock_fd < 0 || flock(*lock_fd, LOCK_EX | LOCK_NB) != 0)
    {
        if (*lock_fd >= 0)
            close(*lock_fd);
        *lock_fd = -1;
        printf("probe no busy\n");
        return -1;
    }
    const char *why = "nolink";
    int version = 0;
    for (size_t i = 0; ec == NULL && i < sizeof(ec_links) / sizeof(ec_links[0]); i++)
    {
        if (ec_links[i]->open() != 0)
            continue;
        ec = ec_links[i];
        why = ht_detect(&version, fans);
        if (why != NULL)
        {
            glog(LOG_INFO, "ec-fanctl: %s link: %s", ec->name, why);
            ec->close();
            ec = NULL;
        }
    }
    if (ec == NULL)
    {
        printf("probe no %s\n", why);
        return -1;
    }
    printf("probe ok %s %d %d", ec->name, version, *fans);
    for (int i = 0; i < *fans; i++)
    {
        int rpm = -1;
        ht_rpm(i, &rpm);
        printf(" %d", rpm);
    }
    printf("\n");
    glog(LOG_INFO, "ec-fanctl: %s link, table %d, %d fans", ec->name, version, *fans);
    return 0;
}

/* "set <d0> [<d1> [<d2>]]": duty 0-255 per fan from fan 0 up. Mode is read
 * every time, so an EC that left manual mode (resume, reset) is re-armed. */
static const char *ec_set(const char *args, int fans, int *last, int *held)
{
    int duty[EC_MAX_FANS];
    int n = sscanf(args, "%d %d %d", &duty[0], &duty[1], &duty[2]);
    if (n < 1 || n > fans)
        return "range";
    for (int i = 0; i < n; i++)
        if (duty[i] < 0 || duty[i] > 255)
            return "range";
    unsigned char mode;
    if (ht_read(EC_REG_MODE, &mode) != 0)
        return "ec";
    if (mode != 1)
    {
        if (ht_write(EC_REG_MODE, 1) != 0)
            return "ec";
        for (int i = 0; i < EC_MAX_FANS; i++)
            last[i] = -1;
        glog(LOG_INFO, "ec-fanctl: manual mode on");
    }
    *held = 1;
    for (int i = 0; i < n; i++)
    {
        if (duty[i] == last[i])
            continue;
        if (ht_write(EC_REG_FAN_SEL, i) != 0 || ht_write(EC_REG_DUTY, duty[i]) != 0)
            return "ec";
        last[i] = duty[i];
    }
    return NULL;
}

/* Back to the BIOS curve: mode 0, the way the MyASUS fan test ends. */
static void ec_release(int *last, int *held, const char *why)
{
    if (!*held)
        return;
    if (ht_write(EC_REG_MODE, 0) != 0)
        glog(LOG_ERR, "ec-fanctl: release (%s) failed", why);
    else
        glog(LOG_INFO, "ec-fanctl: fans back to BIOS (%s)", why);
    *held = 0;
    for (int i = 0; i < EC_MAX_FANS; i++)
        last[i] = -1;
}

/* Stdin lines, one reply line each: "probe" first, then "set ..." about
 * once a second, "quit" at the end. Quit, EOF, a signal or no command for
 * EC_WATCHDOG_S seconds gives the fans back; the next "set" takes them
 * again. */
int do_ec_fanctl(void)
{
    struct sigaction sa = {0};
    sa.sa_handler = ec_on_signal;
    sigaction(SIGTERM, &sa, NULL);
    sigaction(SIGINT, &sa, NULL);
    sigaction(SIGHUP, &sa, NULL);
    /* unbuffered, so poll() sees every line fgets has not read yet */
    setvbuf(stdin, NULL, _IONBF, 0);
    setvbuf(stdout, NULL, _IOLBF, 0);
    glog(LOG_INFO, "ec-fanctl: start");

    int lock_fd = -1, fans = 0, held = 0, idle = 0;
    int last[EC_MAX_FANS] = {-1, -1, -1};
    struct pollfd in = {.fd = STDIN_FILENO, .events = POLLIN};
    char line[128];
    while (!ec_stop)
    {
        int ready = poll(&in, 1, 1000);
        if (ready < 0 && errno != EINTR)
            break;
        if (ready <= 0)
        {
            if (ready == 0 && ++idle == EC_WATCHDOG_S && ec != NULL)
                ec_release(last, &held, "no command");
            continue;
        }
        if (fgets(line, sizeof(line), stdin) == NULL)
            break;
        idle = 0;
#ifdef EC_SIM_HOOK
        if (EC_SIM_HOOK(line))
            continue;
#endif
        if (strncmp(line, "probe", 5) == 0 && lock_fd < 0)
            ec_probe(&lock_fd, &fans);
        else if (strncmp(line, "set ", 4) == 0)
        {
            const char *err = ec == NULL ? "probe" : ec_set(line + 4, fans, last, &held);
            if (err != NULL)
                printf("err %s\n", err);
            else
                printf("ok\n");
        }
        else if (strncmp(line, "quit", 4) == 0)
            break;
        else
            printf("err cmd\n");
    }
    if (ec != NULL)
    {
        ec_release(last, &held, ec_stop ? "signal" : "exit");
        ec->close();
    }
    if (lock_fd >= 0)
        close(lock_fd);
    glog(LOG_INFO, "ec-fanctl: exit");
    return 0;
}
