/* ASUS EC HealthyTable fan control for gpu-helper.
 *
 * AsusSAIO.sys (MyASUS fan test, g-helper Windows experimental build) talks to
 * the EC over a private port pair, 0x25C data / 0x25D command+status, that is
 * separate from the ACPI EC at 0x62/0x66. Protocol and register table were
 * recovered from the driver and validated against the EC RAM aperture on
 * TUF FA506/FA507. Only the fan subset is implemented here. */
#include "gpu-helper.h"
#include <time.h>

#if defined(__x86_64__) || defined(__i386__)
#include <sys/io.h>
#define EC_HAVE_IO 1
#endif

#define EC_DATA 0x25C
#define EC_CMD 0x25D
#define EC_OBF 0x01
#define EC_IBF 0x02

#define EC_PREFIX 0xFF
#define EC_CMD_PROBE 0xBB /* payload 0x50, replies table version */
#define EC_CMD_TABLE 0xDD /* payload dir, reg, value */
#define EC_READ 0x02
#define EC_WRITE 0x82

#define EC_REG_FAN_COUNT 0x30
#define EC_REG_MODE 0x31 /* 0 = EC curve, 1 = manual; one bit for all fans */
#define EC_REG_FAN_SEL 0x32
#define EC_REG_RPM_LO 0x33
#define EC_REG_RPM_HI 0x34
#define EC_REG_DUTY 0x35

#define EC_POLL_LIMIT 1000
#define EC_POLL_US 100
#define EC_GAP_US 20000 /* EC needs settle time after each transaction */
#define EC_MAX_FANS 3

#ifdef EC_HAVE_IO

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

/* Poll status until (status & mask) == want. */
static int ec_wait(int mask, int want)
{
    for (int i = 0; i < EC_POLL_LIMIT; i++)
    {
        if ((inb(EC_CMD) & mask) == want)
            return 0;
        ec_sleep_us(EC_POLL_US);
    }
    return -1;
}

static void ec_drain(void)
{
    for (int i = 0; i < EC_POLL_LIMIT && (inb(EC_CMD) & EC_OBF); i++)
    {
        (void)inb(EC_DATA);
        ec_sleep_us(EC_POLL_US);
    }
}

/* One transaction: prefix, command, payload bytes, optional one-byte reply. */
static int ec_xfer(int cmd, const unsigned char *data, int len, unsigned char *reply)
{
    ec_drain();
    if (ec_wait(EC_IBF, 0) != 0)
        return -1;
    outb(EC_PREFIX, EC_CMD);
    if (ec_wait(EC_IBF, 0) != 0)
        return -1;
    outb((unsigned char)cmd, EC_CMD);
    for (int i = 0; i < len; i++)
    {
        if (ec_wait(EC_IBF, 0) != 0)
            return -1;
        outb(data[i], EC_DATA);
    }
    if (ec_wait(EC_IBF, 0) != 0)
        return -1;
    if (reply != NULL)
    {
        if (ec_wait(EC_OBF, EC_OBF) != 0)
            return -1;
        *reply = inb(EC_DATA);
    }
    ec_sleep_us(EC_GAP_US);
    return 0;
}

static int ht_probe(unsigned char *version)
{
    const unsigned char d[] = {0x50};
    if (ec_xfer(EC_CMD_PROBE, d, 1, version) != 0)
        return -1;
    return *version == 0 || *version == 0xFF ? -1 : 0;
}

static int ht_read(int reg, unsigned char *value)
{
    const unsigned char d[] = {EC_READ, (unsigned char)reg, 0};
    return ec_xfer(EC_CMD_TABLE, d, 3, value);
}

static int ht_write(int reg, int value)
{
    const unsigned char d[] = {EC_WRITE, (unsigned char)reg, (unsigned char)value};
    return ec_xfer(EC_CMD_TABLE, d, 3, NULL);
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

/* Reply: "probe <version> <fan count> <rpm0> <rpm1>", or "probe -" when the
 * EC does not answer. The caller compares rpm0 against the hwmon tach to be
 * sure this really is the HealthyTable before any write. */
static void ec_probe(void)
{
    unsigned char ver, count;
    int rpm0 = -1, rpm1 = -1;
    if (ht_probe(&ver) != 0 || ht_read(EC_REG_FAN_COUNT, &count) != 0 || ht_rpm(0, &rpm0) != 0)
    {
        printf("probe -\n");
        return;
    }
    if (count > 1)
        ht_rpm(1, &rpm1);
    printf("probe %u %u %d %d\n", ver, count, rpm0, rpm1);
}

#endif /* EC_HAVE_IO */

/* Manual fan loop. Stdin lines: "probe", "set <fan> <duty>" (fan 0-2, duty
 * 0-255, 0 stops the fan), "auto", "quit". One reply line each. Duty is only
 * rewritten when it changes; mode is read back every set so an EC that fell
 * out of manual (resume, EC reset) gets re-armed. Quit, EOF or a signal
 * releases the fans back to the EC curve. */
int do_ec_fanctl(void)
{
#ifndef EC_HAVE_IO
    printf("err arch\n");
    return 1;
#else
    if (ioperm(EC_DATA, 2, 1) != 0)
    {
        glog(LOG_ERR, "ec-fanctl: ioperm: %s", strerror(errno));
        printf("err ioperm\n");
        return 3;
    }
    struct sigaction sa = {0};
    sa.sa_handler = ec_on_signal;
    sigaction(SIGTERM, &sa, NULL);
    sigaction(SIGINT, &sa, NULL);
    sigaction(SIGHUP, &sa, NULL);
    setvbuf(stdout, NULL, _IOLBF, 0);
    glog(LOG_INFO, "ec-fanctl: start");

    int touched = 0;
    int last_duty[EC_MAX_FANS] = {-1, -1, -1};
    char line[128];
    while (!ec_stop && fgets(line, sizeof(line), stdin))
    {
        int fan, duty;
        if (sscanf(line, "set %d %d", &fan, &duty) == 2)
        {
            unsigned char mode;
            if (fan < 0 || fan >= EC_MAX_FANS || duty < 0 || duty > 255)
            {
                printf("err range\n");
                continue;
            }
            if (ht_read(EC_REG_MODE, &mode) != 0)
            {
                printf("err ec\n");
                continue;
            }
            if (mode != 1)
            {
                if (ht_write(EC_REG_MODE, 1) != 0)
                {
                    printf("err ec\n");
                    continue;
                }
                for (int i = 0; i < EC_MAX_FANS; i++)
                    last_duty[i] = -1;
                touched = 1;
                glog(LOG_INFO, "ec-fanctl: manual mode on");
            }
            if (duty != last_duty[fan])
            {
                if (ht_write(EC_REG_FAN_SEL, fan) != 0 || ht_write(EC_REG_DUTY, duty) != 0)
                {
                    printf("err ec\n");
                    continue;
                }
                last_duty[fan] = duty;
            }
            printf("ok\n");
        }
        else if (strncmp(line, "probe", 5) == 0)
            ec_probe();
        else if (strncmp(line, "auto", 4) == 0)
        {
            if (ht_write(EC_REG_MODE, 0) != 0)
                printf("err ec\n");
            else
                printf("ok\n");
            for (int i = 0; i < EC_MAX_FANS; i++)
                last_duty[i] = -1;
            touched = 0;
        }
        else if (strncmp(line, "quit", 4) == 0)
            break;
        else
            printf("err cmd\n");
    }
    if (touched)
    {
        ht_write(EC_REG_MODE, 0);
        glog(LOG_INFO, "ec-fanctl: fans released to EC");
    }
    ioperm(EC_DATA, 2, 0);
    glog(LOG_INFO, "ec-fanctl: exit");
    return 0;
#endif
}
