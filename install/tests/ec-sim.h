/* Simulated ASUS EC for the ec-fanctl test build (test-gpu-helper.sh).
 * vendor/gpu-helper/ec_ops.c includes it when GH_EC_SIM is set. It answers
 * on both links the way the ASUS Windows code drives them:
 *   lpc  AsusSAIO.sys: drain OBF, 0xFF then the command byte to 0x25D,
 *        payload to 0x25C, reply byte on 0x25C with OBF set
 *   hid  AsusEC DLL: 64-byte feature report 7 [7, cmd, reply flag,
 *        payload], get feature [7, cmd, length, status 0x80, data]
 * EC_SIM="<lpc|hid|both|none> <version> <vendor bit> <fans> <mode> [fault]"
 *   fault 1: a stale byte waits in the lpc output buffer
 *   fault 2: hid get feature answers status 0x82 (invalid parameter)
 * Table writes go to stderr as "w <reg> <value>", protocol errors as
 * "bad ...". Stdin "sim reset" makes the EC leave manual mode by itself. */
#include <linux/hidraw.h>
#include <sys/ioctl.h>

#define EC_HAVE_IO 1
#define EC_LOCK GH_EC_SIM "/ec-fan.lock"
#define EC_HIDRAW_DIR GH_EC_SIM "/sys"
#define EC_DEV_DIR GH_EC_SIM "/dev"
#define EC_HID_GAP_US 1000

static struct
{
    int loaded, lpc, hid, version, vendor, fans, mode, fault, sel, duty[8];
    int stage, cmd, need, got, obf, out; /* lpc */
    int pending, reply;                  /* hid */
    unsigned char payload[8];
} sim;

static void sim_load(void)
{
    char link[8] = "";
    const char *cfg = getenv("EC_SIM");
    if (sim.loaded)
        return;
    sim.loaded = 1;
    if (cfg == NULL || sscanf(cfg, "%7s %d %d %d %d %d", link, &sim.version, &sim.vendor,
                              &sim.fans, &sim.mode, &sim.fault) < 5)
        return;
    sim.lpc = !strcmp(link, "lpc") || !strcmp(link, "both");
    sim.hid = !strcmp(link, "hid") || !strcmp(link, "both");
    if (sim.lpc && sim.fault == 1)
    {
        sim.obf = 1;
        sim.out = 0x5A;
    }
}

/* HealthyTable: 1 = reply in *reply, 0 = write done, -1 = rejected. */
static int sim_exec(int cmd, const unsigned char *d, int len, int *reply)
{
    if (cmd == 0xBB && len == 1)
    {
        *reply = d[0] == 0x50 ? sim.version : d[0] == 0x51 ? sim.vendor << 7 : 0;
        return 1;
    }
    if (cmd != 0xDD || len != 3 || (d[0] != 0x02 && d[0] != 0x82))
    {
        fprintf(stderr, "bad command %02x\n", cmd);
        return -1;
    }
    if (d[0] == 0x82)
    {
        fprintf(stderr, "w %02x %02x\n", d[1], d[2]);
        if (d[1] == 0x31)
            sim.mode = d[2];
        else if (d[1] == 0x32)
            sim.sel = d[2] & 7;
        else if (d[1] == 0x35)
            sim.duty[sim.sel] = d[2];
        return 0;
    }
    int rpm = 1000 + sim.duty[sim.sel] * 20;
    switch (d[1])
    {
    case 0x30:
        *reply = sim.fans;
        return 1;
    case 0x31:
        *reply = sim.mode;
        return 1;
    case 0x33:
        *reply = rpm & 0xFF;
        return 1;
    case 0x34:
        *reply = rpm >> 8;
        return 1;
    }
    fprintf(stderr, "bad read %02x\n", d[1]);
    return -1;
}

static int sim_ioperm(unsigned long from, unsigned long num, int on)
{
    (void)on;
    sim_load();
    return from == 0x25C && num == 2 ? 0 : -1;
}

/* Status 0x25D: OBF bit 0, IBF bit 1 (always clear here). No lpc EC reads
 * as a floating bus, 0xFF. */
static unsigned char sim_inb(unsigned short port)
{
    sim_load();
    if (!sim.lpc)
        return 0xFF;
    if (port == 0x25D)
        return sim.obf ? 1 : 0;
    sim.obf = 0;
    return sim.out;
}

static void sim_outb(unsigned char v, unsigned short port)
{
    if (!sim.lpc)
        return;
    if (port == 0x25D)
    {
        if (sim.stage == 0 && v == 0xFF)
            sim.stage = 1;
        else if (sim.stage == 1 && (v == 0xBB || v == 0xDD))
        {
            sim.cmd = v;
            sim.need = v == 0xBB ? 1 : 3;
            sim.got = 0;
            sim.stage = 2;
        }
        else
        {
            fprintf(stderr, "bad lpc command byte %02x\n", v);
            sim.stage = 0;
        }
        return;
    }
    if (sim.stage != 2)
    {
        fprintf(stderr, "bad lpc data byte %02x\n", v);
        return;
    }
    if (sim.obf)
        fprintf(stderr, "bad lpc: stale output not drained\n");
    sim.payload[sim.got++] = v;
    if (sim.got < sim.need)
        return;
    int reply = 0;
    sim.stage = 0;
    if (sim_exec(sim.cmd, sim.payload, sim.need, &reply) == 1)
    {
        sim.out = reply;
        sim.obf = 1;
    }
}

static int sim_ioctl(int fd, unsigned long req, unsigned char *buf)
{
    (void)fd;
    sim_load();
    if (!sim.hid || buf[0] != 7)
        return -1;
    if (req == HIDIOCSFEATURE(64))
    {
        int reply = 0;
        int r = sim_exec(buf[1], buf + 3, buf[1] == 0xBB ? 1 : 3, &reply);
        if (r < 0)
            return -1;
        sim.pending = buf[2] && r == 1;
        sim.reply = reply;
        return 64;
    }
    if (req != HIDIOCGFEATURE(64))
    {
        fprintf(stderr, "bad hid request %lx\n", req);
        return -1;
    }
    memset(buf + 1, 0, 63);
    if (!sim.pending || sim.fault == 2)
    {
        buf[3] = 0x82;
        return 64;
    }
    buf[2] = 1;
    buf[3] = 0x80;
    buf[4] = sim.reply;
    sim.pending = 0;
    return 64;
}

static int sim_hook(const char *line)
{
    if (strncmp(line, "sim reset", 9) != 0)
        return 0;
    sim.mode = 0;
    return 1;
}

#define inb(port) sim_inb(port)
#define outb(value, port) sim_outb(value, port)
#define ioperm(from, num, on) sim_ioperm(from, num, on)
#define ioctl(fd, req, buf) sim_ioctl(fd, req, buf)
#define EC_SIM_HOOK(line) sim_hook(line)
