using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace GHelper.Linux.USB;

/// <summary>
/// Native Linux hidraw device discovery and I/O.
///
/// HidSharpCore only discovers USB-HID devices (it calls
/// udev_device_get_parent_with_subsystem_devtype("usb","usb_device")
/// and discards anything without a USB parent). This means I2C-HID
/// devices - like the ASUS TUF FA608PP keyboard (ITE51368:00 0B05:19B6)
/// - are completely invisible.
///
/// This helper scans /dev/hidraw* directly using ioctl(HIDIOCGRAWINFO)
/// to get vendor/product IDs regardless of bus type (USB, I2C, SPI).
/// It provides raw write + SetFeature capabilities for AURA protocol.
///
/// Reference:
///   Linux kernel: include/uapi/linux/hidraw.h
///   AURA protocol: report ID 0x5D, SetFeature for mode/color/brightness
/// </summary>
public static class HidrawHelper
{
    // ioctl constants (from linux/hidraw.h)

    // _IOR('H', 0x03, struct hidraw_devinfo)  - get bus/vendor/product
    private const uint HIDIOCGRAWINFO = 0x80084803;
    // _IOR('H', 0x01, int)  - get report descriptor size
    private const uint HIDIOCGRDESCSIZE = 0x80044801;
    // _IOR('H', 0x02, struct hidraw_report_descriptor)  - get report descriptor
    private const uint HIDIOCGRDESC = 0x90044802;

    // HIDIOCSFEATURE = _IOWR('H', 0x06, len)  - send SetFeature report
    // The size field is variable; we encode it at call time.
    private static uint HIDIOCSFEATURE(int size)
    {
        return (uint)(0xC0004806 | ((size & 0x3FFF) << 16));
    }

    // HIDIOCGFEATURE = _IOWR('H', 0x07, len)  - read GetFeature report
    private static uint HIDIOCGFEATURE(int size)
    {
        return (uint)(0xC0004807 | ((size & 0x3FFF) << 16));
    }

    // Bus type constants from linux/input.h
    private const ushort BUS_USB = 0x03;
    private const ushort BUS_I2C = 0x18;
    private const ushort BUS_SPI = 0x1C;

    // ASUS vendor ID
    private const ushort ASUS_VENDOR_ID = 0x0B05;
    // sysfs idVendor file format: 4-char lowercase hex, no 0x prefix
    private const string ASUS_VENDOR_HEX = "0b05";

    // AsusHid.ALL_PIDS + 2024 Slash bar; used for probes and writes on any bus
    private static readonly HashSet<ushort> AuraProductIds =
        AsusHid.ALL_PIDS.Select(p => (ushort)p).Append(AnimeMatrix.SlashDevice.ProductIdStandard).ToHashSet();

    // P/Invoke

    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPStr)] string pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, uint request, ref HidrawDevinfo data);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, uint request, ref int data);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, uint request, ref HidrawReportDescriptor data);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, uint request, byte[] data);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int fd, byte[] buf, nint count);

    private const int O_RDWR = 0x02;
    private const int O_NONBLOCK = 0x800;

    // Structs

    [StructLayout(LayoutKind.Sequential)]
    private struct HidrawDevinfo
    {
        public uint bustype;   // __u32
        public short vendor;   // __s16
        public short product;  // __s16
    }

    /// <summary>
    /// Matches kernel struct hidraw_report_descriptor { __u32 size; __u8 value[HID_MAX_DESCRIPTOR_SIZE]; }
    /// Must use [MarshalAs] for correct pinning/layout - a raw byte[] doesn't work
    /// reliably with ioctl because the marshaller may copy instead of pin.
    /// See HidSharpCore's NativeMethods.hidraw_report_descriptor for reference.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct HidrawReportDescriptor
    {
        public uint size;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4096)]
        public byte[] value;
    }

    /// <summary>
    /// Information about a discovered hidraw device.
    /// </summary>
    public class HidrawDeviceInfo
    {
        public string Path { get; init; } = "";
        public ushort Vendor { get; init; }
        public ushort Product { get; init; }
        public ushort BusType { get; init; }
        public bool HasAuraReport { get; init; }

        public string BusName => BusType switch
        {
            BUS_USB => "USB",
            BUS_I2C => "I2C",
            BUS_SPI => "SPI",
            5 => "Bluetooth",
            _ => $"bus=0x{BusType:X2}"
        };

        public bool IsI2C => BusType == BUS_I2C;
        public bool IsUSB => BusType == BUS_USB;
    }

    // Cache

    private static List<HidrawDeviceInfo>? _cachedDevices;
    private static readonly object _cacheLock = new();

    /// <summary>
    /// Enumerate all ASUS hidraw devices on the system, regardless of bus type.
    /// Results are cached for the lifetime of the process.
    ///
    /// </summary>
    public static IReadOnlyList<HidrawDeviceInfo> EnumerateAsusDevices()
    {
        lock (_cacheLock)
        {
            if (_cachedDevices != null)
                return _cachedDevices;

            _cachedDevices = new List<HidrawDeviceInfo>();

            // Pass 1: probe all ASUS hidraws, bucket by USB parent syspath.
            var byParent = new Dictionary<string, List<HidrawDeviceInfo>>();
            var noParent = new List<HidrawDeviceInfo>(); // I2C-HID, no usb_device parent

            try
            {
                for (int i = 0; i < 32; i++)
                {
                    string path = $"/dev/hidraw{i}";
                    if (!File.Exists(path))
                        continue;

                    var info = ProbeDevice(path);
                    if (info == null || info.Vendor != ASUS_VENDOR_ID)
                        continue;

                    var parent = GetUsbParentSyspath(path);
                    if (parent == null)
                    {
                        noParent.Add(info);
                        continue;
                    }

                    if (!byParent.TryGetValue(parent, out var group))
                        byParent[parent] = group = new List<HidrawDeviceInfo>();
                    group.Add(info);
                }
            }
            catch (Exception ex)
            {
                Helpers.Logger.WriteLine($"HidrawHelper: enumeration error: {ex.Message}");
            }

            // Pass 2: pick the best candidate per USB parent group.
            foreach (var group in byParent.Values)
            {
                // Prefer the first Aura-capable interface; fall back to first in list.
                var pick = group.FirstOrDefault(d => d.HasAuraReport) ?? group[0];

                _cachedDevices.Add(pick);
                Helpers.Logger.WriteLine(
                    $"HidrawHelper: found ASUS device {pick.Path} PID=0x{pick.Product:X4} Bus={pick.BusName} Aura={pick.HasAuraReport}");

                foreach (var sibling in group)
                {
                    if (sibling == pick)
                        continue;
                    Helpers.Logger.WriteLine(
                        $"HidrawHelper: skipping {sibling.Path} - USB parent {GetUsbParentSyspath(sibling.Path)} already enumerated (kept {pick.Path} Aura={pick.HasAuraReport})");
                }
            }

            // I2C-HID and other non-USB devices: no dedup needed.
            foreach (var info in noParent)
            {
                _cachedDevices.Add(info);
                Helpers.Logger.WriteLine(
                    $"HidrawHelper: found ASUS device {info.Path} PID=0x{info.Product:X4} Bus={info.BusName} Aura={info.HasAuraReport}");
            }

            return _cachedDevices;
        }
    }

    /// <summary>
    /// Returns true if <paramref name="hidrawDevPath"/>'s physical USB parent
    /// has already been seen in this enumeration. Logs the skip when true.
    /// I2C-HID and devices without a resolvable USB parent always return false
    /// (no dedup) so they pass through unchanged.
    /// </summary>
    public static bool IsDuplicateUsbDevice(string hidrawDevPath, HashSet<string> seen, string logPrefix)
    {
        var parent = GetUsbParentSyspath(hidrawDevPath);
        if (parent == null || seen.Add(parent))
            return false;

        Helpers.Logger.WriteLine(
            $"{logPrefix}: skipping {hidrawDevPath} - USB parent {parent} already enumerated");
        return true;
    }

    /// <summary>
    /// Mirrors asusctl 6.3.7's seen_usb_parents logic
    /// (asusd/src/aura_manager.rs, GitLab 9cbf643b):
    ///   "A USB device can expose multiple HID interfaces (and thus multiple
    ///    hidraw nodes). Processing more than one causes duplicate device
    ///    initialisation which can interfere with the kernel's own HID driver
    ///    and trigger a USB reset loop."
    ///
    /// </summary>
    public static string? GetUsbParentSyspath(string hidrawDevPath)
    {
        try
        {
            var name = Path.GetFileName(hidrawDevPath);
            if (!name.StartsWith("hidraw", StringComparison.Ordinal))
                return null;

            var sysClassPath = $"/sys/class/hidraw/{name}";
            if (!Directory.Exists(sysClassPath))
                return null;

            // /sys/class/hidraw/hidrawN is a symlink into /sys/devices/.../hidraw/hidrawN.
            // ResolveLinkTarget(true) chases the symlink chain via realpath().
            var resolved = new DirectoryInfo(sysClassPath).ResolveLinkTarget(true) as DirectoryInfo;
            var dir = resolved ?? new DirectoryInfo(sysClassPath);

            // Walk up parents looking for DEVTYPE=usb_device. Bounded by both
            // the /sys/ prefix and an explicit depth cap to prevent any runaway
            // loop on weird filesystem shapes (sysfs depth is normally 6-8).
            for (int depth = 0;
                 depth < 16
                 && dir != null
                 && dir.FullName.StartsWith("/sys/", StringComparison.Ordinal);
                 depth++, dir = dir.Parent)
            {
                if (IsUsbDeviceNode(dir.FullName, out var idVendor))
                    return idVendor == ASUS_VENDOR_HEX ? dir.FullName : null;
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"HidrawHelper.GetUsbParentSyspath({hidrawDevPath}): {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Returns true if <paramref name="sysDir"/>'s uevent declares
    /// DEVTYPE=usb_device. Sets <paramref name="idVendor"/> from idVendor file
    /// if present (null if unreadable). Returns false for non-USB nodes,
    /// missing/unreadable uevents, or any I/O failure.
    /// </summary>
    private static bool IsUsbDeviceNode(string sysDir, out string? idVendor)
    {
        idVendor = null;
        try
        {
            var ueventPath = Path.Combine(sysDir, "uevent");
            if (!File.Exists(ueventPath))
                return false;

            bool isUsbDevice = false;
            foreach (var line in File.ReadAllLines(ueventPath))
            {
                if (line.Trim() == "DEVTYPE=usb_device")
                {
                    isUsbDevice = true;
                    break;
                }
            }
            if (!isUsbDevice)
                return false;

            var vendorPath = Path.Combine(sysDir, "idVendor");
            if (File.Exists(vendorPath))
                idVendor = File.ReadAllText(vendorPath).Trim();
            return true;
        }
        catch
        {
            // Treat any read failure as "not a usable usb_device node" so the
            // caller continues walking up rather than committing to dedup.
            return false;
        }
    }

    /// <summary>
    /// Invalidate the cached device list (e.g., after udev event).
    /// </summary>
    public static void InvalidateCache()
    {
        lock (_cacheLock)
        { _cachedDevices = null; }
    }

    /// <summary>
    /// Check if any ASUS AURA-capable device is available (including I2C-HID).
    /// </summary>
    public static bool HasAsusAuraDevice()
    {
        return EnumerateAsusDevices().Any(d =>
            AuraProductIds.Contains(d.Product) && d.HasAuraReport);
    }

    /// <summary>
    /// Get all ASUS AURA-capable device paths.
    /// </summary>
    public static IEnumerable<string> GetAuraDevicePaths()
    {
        return EnumerateAsusDevices()
            .Where(d => AuraProductIds.Contains(d.Product) && d.HasAuraReport)
            .Select(d => d.Path);
    }

    /// <summary>
    /// Write multiple AURA messages to all discovered ASUS AURA devices.
    /// Uses SetFeature ioctl (required for feature reports on hidraw).
    /// </summary>
    public static bool WriteAll(byte reportId, List<byte[]> messages, string? log = null)
    {
        // Serialize with all other ASUS HID traffic (upstream #5784)
        lock (AsusHid.HidLock)
            return WriteAllLocked(reportId, messages, log);
    }

    private static bool WriteAllLocked(byte reportId, List<byte[]> messages, string? log)
    {
        var paths = GetAuraDevicePaths().ToList();
        if (paths.Count == 0)
            return false;

        bool anySuccess = false;
        foreach (var path in paths)
        {
            int fd = -1;
            try
            {
                fd = open(path, O_RDWR);
                if (fd < 0)
                {
                    Helpers.Logger.WriteLine($"HidrawHelper: cannot open {path}: errno={Marshal.GetLastPInvokeError()}");
                    continue;
                }

                foreach (var data in messages)
                {
                    if (WriteToFd(fd, data, path, log))
                        anySuccess = true;
                }
            }
            catch (Exception ex)
            {
                Helpers.Logger.WriteLine($"HidrawHelper: error writing to {path}: {ex.Message}");
            }
            finally
            {
                if (fd >= 0)
                    close(fd);
            }
        }

        return anySuccess;
    }

    /// <summary>
    /// Write a single message to all AURA devices.
    /// </summary>
    public static bool WriteAll(byte reportId, byte[] data, string? log = null)
    {
        return WriteAll(reportId, new List<byte[]> { data }, log);
    }

    /// <summary>
    /// Query AURA device capabilities via SetFeature(0x05) + GetFeature(0x5D).
    /// Returns raw 64-byte response, or null on failure.
    /// Protocol (from Armoury Crate decompilation of AacNBDTHal):
    ///   1. SetFeature [0x5D, 0x05, 0x20, 0x31, 0x00, queryLastByte] - query command
    ///   2. GetFeature [0x5D, ...] - read back capability response
    /// Response bytes of interest:
    ///   [9]  = KBBackLightType (0=single, 1=minimal, 2=multi-zone, 3=per-key, 4=four-zone)
    ///   [10] = Keyboard year (>= 0x23 enables extended fields [17])
    ///   [13] = Feat1 bitfield (bit0=Logo, bit1=Lightbar, bit4=VCut, bit5=Aero, bit6=Bump, bit7=Rearglow)
    ///   [14] = Feat2 bitfield (bit2=DefaultColor, bit3=RGBWheel, bit4=OneZoneRedEffect, bit6=PerKeyMap)
    ///   [17] = Model series/family (1=Strix, 2=Flow, 4=Zephyrus, 8=TUF, 0x10=NR2301/SE, 0x20=Desktop)
    ///   [18]-[23] = LED counts per zone (Lightbar, Logo, Aero, VCut, Rearglow, Bump)
    /// <para>
    /// <paramref name="queryLastByte"/> is the 6th query byte. Upstream G-Helper
    /// uses 0x20; the Armoury Crate decompilation referenced 0x1A.
    /// Both have been observed to return identical capability responses on Strix
    /// hardware, but firmware variants may prefer one - <see cref="USB.Aura"/>'s
    /// detector tries 0x20 first then falls back to 0x1A.
    /// </para>
    /// </summary>
    public static byte[]? QueryAuraCapabilities(byte queryLastByte = 0x20)
    {
        // Two-step SetFeature+GetFeature; keep atomic vs other HID traffic
        lock (AsusHid.HidLock)
            return QueryAuraCapabilitiesLocked(queryLastByte);
    }

    private static byte[]? QueryAuraCapabilitiesLocked(byte queryLastByte)
    {
        var path = GetAuraDevicePaths().FirstOrDefault();
        if (path == null)
        {
            Helpers.Logger.WriteLine("AURA GetFeature: no AURA device found");
            return null;
        }

        int fd = -1;
        try
        {
            fd = open(path, O_RDWR);
            if (fd < 0)
            {
                Helpers.Logger.WriteLine($"AURA GetFeature: cannot open {path}: errno={Marshal.GetLastPInvokeError()}");
                return null;
            }

            // Send the 0x05 query via SetFeature (targeted to this specific device)
            byte[] query = new byte[64];
            query[0] = 0x5D;  // report ID
            query[1] = 0x05;
            query[2] = 0x20;
            query[3] = 0x31;
            query[4] = 0x00;
            query[5] = queryLastByte;
            int ret = ioctl(fd, HIDIOCSFEATURE(64), query);
            if (ret < 0)
            {
                Helpers.Logger.WriteLine($"AURA GetFeature: SetFeature(0x05,..,0x{queryLastByte:X2}) failed on {path}: errno={Marshal.GetLastPInvokeError()}");
                return null;
            }

            // Small delay for firmware to prepare response
            Thread.Sleep(50);

            // Read back via GetFeature - report ID must be pre-set in buffer
            byte[] response = new byte[64];
            response[0] = 0x5D;  // report ID
            ret = ioctl(fd, HIDIOCGFEATURE(64), response);
            if (ret < 0)
            {
                Helpers.Logger.WriteLine($"AURA GetFeature: GetFeature(0x5D) failed on {path}: errno={Marshal.GetLastPInvokeError()}");
                return null;
            }

            Helpers.Logger.WriteLine($"AURA GetFeature OK on {path} (q=0x{queryLastByte:X2}): {BitConverter.ToString(response, 0, Math.Min(ret, 32))}");
            return response;
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"AURA GetFeature: exception on {path}: {ex.Message}");
            return null;
        }
        finally
        {
            if (fd >= 0)
                close(fd);
        }
    }

    /// <summary>
    /// Probe M-key firmware binding slots. Sends the query
    /// <c>[AURA_ID, 0x9F, 0x02, 0x00]</c> via SetFeature then polls GetFeature
    /// for a reply whose bytes [1][2] echo 0x9F 0x02; byte [4] is the slot
    /// count and bytes [5..5+count] are the per-slot default EC opcodes.
    /// Returns the defaults array, or null if unsupported. Port of the probe
    /// half of upstream MKeyControl (#5691). Works on both USB and I2C AURA
    /// nodes since Linux hidraw is bus-agnostic.
    /// </summary>
    public static byte[]? MKeyProbe()
    {
        lock (AsusHid.HidLock)
            return MKeyProbeLocked();
    }

    private static byte[]? MKeyProbeLocked()
    {
        var path = GetAuraDevicePaths().FirstOrDefault();
        if (path == null)
            return null;

        int fd = -1;
        try
        {
            fd = open(path, O_RDWR);
            if (fd < 0)
            {
                Helpers.Logger.WriteLine($"MKey probe: cannot open {path}: errno={Marshal.GetLastPInvokeError()}");
                return null;
            }

            byte[] query = new byte[64];
            query[0] = AsusHid.AURA_ID;
            query[1] = 0x9F;
            query[2] = 0x02;
            query[3] = 0x00;
            if (ioctl(fd, HIDIOCSFEATURE(64), query) < 0)
            {
                Helpers.Logger.WriteLine($"MKey probe: SetFeature failed on {path}: errno={Marshal.GetLastPInvokeError()}");
                return null;
            }

            for (int i = 0; i < 10; i++)
            {
                Thread.Sleep(20);
                byte[] response = new byte[64];
                response[0] = AsusHid.AURA_ID;
                if (ioctl(fd, HIDIOCGFEATURE(64), response) < 0)
                    continue;

                if (response[1] != 0x9F || response[2] != 0x02)
                    continue;
                int count = response[4];
                if (count < 1 || count > 8 || 5 + count > 64)
                    continue;

                Helpers.Logger.WriteLine($"MKey bindings: {BitConverter.ToString(response, 0, 16)}");
                return response[5..(5 + count)];
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"MKey probe error: {ex.Message}");
        }
        finally
        {
            if (fd >= 0)
                close(fd);
        }
        return null;
    }

    public static void DisableBacklightOobe()
    {
        foreach (var dev in EnumerateAsusDevices())
        {
            if (dev.Product != 0x19B6 || !dev.IsI2C)
                continue;
            int fd = open(dev.Path, O_RDWR);
            if (fd < 0)
                continue;
            try
            {
                byte[] buf = { 0x46, 0x01 };
                if (ioctl(fd, HIDIOCSFEATURE(buf.Length), buf) >= 0)
                    Helpers.Logger.WriteLine($"HidrawHelper: backlight OOBE disabled on {dev.Path} PID=0x{dev.Product:X4}");
            }
            finally { close(fd); }
        }
    }

    // Internal

    /// <summary>
    /// Probe a single /dev/hidraw* device for vendor/product info and AURA capability.
    /// </summary>
    private static HidrawDeviceInfo? ProbeDevice(string path)
    {
        int fd = -1;
        try
        {
            fd = open(path, O_RDWR | O_NONBLOCK);
            if (fd < 0)
            {
                // Try read-only for probing (might lack write perms)
                fd = open(path, 0 /* O_RDONLY */ | O_NONBLOCK);
                if (fd < 0)
                    return null;
            }

            // Get device info (bus, vendor, product)
            var devinfo = new HidrawDevinfo();
            if (ioctl(fd, HIDIOCGRAWINFO, ref devinfo) < 0)
                return null;

            ushort vid = (ushort)devinfo.vendor;
            ushort pid = (ushort)devinfo.product;
            ushort bus = (ushort)devinfo.bustype;

            // Check for AURA report (report ID 0x5D) in descriptor
            bool hasAura = false;
            if (vid == ASUS_VENDOR_ID && AuraProductIds.Contains(pid))
            {
                hasAura = CheckReportDescriptorForAura(fd, bus, pid);
            }

            return new HidrawDeviceInfo
            {
                Path = path,
                Vendor = vid,
                Product = pid,
                BusType = bus,
                HasAuraReport = hasAura
            };
        }
        catch
        {
            return null;
        }
        finally
        {
            if (fd >= 0)
                close(fd);
        }
    }

    /// <summary>
    /// Check if the HID report descriptor contains report ID 0x5D (AURA_ID).
    /// Uses a proper [StructLayout] struct for the ioctl call (matching HidSharpCore).
    /// For known ASUS product IDs on I2C, falls back to assuming AURA support
    /// if the descriptor check fails (some I2C-HID devices don't declare 0x5D).
    /// </summary>
    private static bool CheckReportDescriptorForAura(int fd, ushort busType, ushort pid)
    {
        try
        {
            // Get descriptor size
            int descSize = 0;
            if (ioctl(fd, HIDIOCGRDESCSIZE, ref descSize) < 0)
            {
                int err = Marshal.GetLastPInvokeError();
                Helpers.Logger.WriteLine($"HidrawHelper: HIDIOCGRDESCSIZE failed: errno={err}");
                return FallbackForI2c(busType, pid, "HIDIOCGRDESCSIZE failed");
            }
            if (descSize <= 0 || descSize > 4096)
            {
                Helpers.Logger.WriteLine($"HidrawHelper: descriptor size out of range: {descSize}");
                return FallbackForI2c(busType, pid, $"descriptor size={descSize}");
            }

            Helpers.Logger.WriteLine($"HidrawHelper: descriptor size={descSize} bytes");

            // Get descriptor using proper struct (matches kernel hidraw_report_descriptor)
            var desc = new HidrawReportDescriptor { size = (uint)descSize };
            if (ioctl(fd, HIDIOCGRDESC, ref desc) < 0)
            {
                int err = Marshal.GetLastPInvokeError();
                Helpers.Logger.WriteLine($"HidrawHelper: HIDIOCGRDESC failed: errno={err}");
                return FallbackForI2c(busType, pid, "HIDIOCGRDESC failed");
            }

            // Log first 32 bytes for debugging
            if (desc.value != null && desc.value.Length > 0)
            {
                int logLen = Math.Min(descSize, 32);
                Helpers.Logger.WriteLine($"HidrawHelper: descriptor[0..{logLen}]: {BitConverter.ToString(desc.value, 0, logLen)}");
            }
            else
            {
                Helpers.Logger.WriteLine("HidrawHelper: descriptor value is null/empty after ioctl");
                return FallbackForI2c(busType, pid, "descriptor value null");
            }

            // Parse the raw HID report descriptor looking for Report ID 0x5D
            // HID Report ID item: 0x85 (1-byte item, type=Global, tag=Report ID) followed by the ID byte
            for (int i = 0; i < descSize - 1; i++)
            {
                if (desc.value[i] == 0x85 && desc.value[i + 1] == AsusHid.AURA_ID)
                {
                    Helpers.Logger.WriteLine($"HidrawHelper: found AURA report ID 0x{AsusHid.AURA_ID:X2} at descriptor offset {i}");
                    return true;
                }
            }

            // Descriptor parsed successfully but 0x5D not found
            Helpers.Logger.WriteLine($"HidrawHelper: report ID 0x{AsusHid.AURA_ID:X2} not found in descriptor ({descSize} bytes)");
            return FallbackForI2c(busType, pid, "0x5D not in descriptor");
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"HidrawHelper: CheckReportDescriptorForAura exception: {ex.Message}");
            return FallbackForI2c(busType, pid, $"exception: {ex.Message}");
        }
    }

    /// <summary>
    /// For known ASUS AURA product IDs on I2C bus, assume AURA is supported
    /// even when the descriptor check fails. This handles I2C-HID devices
    /// (like the TUF FA608PP) where the AURA report may not be formally
    /// declared in the descriptor but the device still responds to AURA protocol.
    /// USB devices are NOT given this fallback - descriptor must match.
    /// </summary>
    private static bool FallbackForI2c(ushort busType, ushort pid, string reason)
    {
        if (busType == BUS_I2C && AuraProductIds.Contains(pid))
        {
            Helpers.Logger.WriteLine(
                $"HidrawHelper: I2C fallback - assuming AURA support for known PID 0x{pid:X4} ({reason})");
            return true;
        }
        return false;
    }

    /// <summary>
    /// Write data to an open hidraw fd.
    /// Tries SetFeature ioctl first (required for feature reports),
    /// falls back to raw write() (for output reports).
    /// </summary>
    private static bool WriteToFd(int fd, byte[] data, string path, string? log)
    {
        return WriteToFdSized(fd, data, 64, path, log);
    }

    /// <summary>
    /// Write data to an open hidraw fd with a caller-provided report size.
    /// AURA uses 64-byte reports; XG Mobile uses 300-byte reports. Same
    /// SetFeature-then-write fallback as <see cref="WriteToFd"/>.
    /// </summary>
    private static bool WriteToFdSized(int fd, byte[] data, int reportSize, string path, string? log)
    {
        byte[] padded = new byte[reportSize];
        Array.Copy(data, padded, Math.Min(data.Length, reportSize));

        // Try SetFeature first (this is how feature-report protocols work)
        int ret = ioctl(fd, HIDIOCSFEATURE(reportSize), padded);
        if (ret >= 0)
        {
            if (log != null)
                Helpers.Logger.WriteLine($"HidrawHelper SetFeature[{reportSize}] {log}: {BitConverter.ToString(data, 0, Math.Min(data.Length, 17))}");
            return true;
        }

        // Fall back to raw write
        nint written = write(fd, padded, reportSize);
        if (written >= 0)
        {
            if (log != null)
                Helpers.Logger.WriteLine($"HidrawHelper Write[{reportSize}] {log}: {BitConverter.ToString(data, 0, Math.Min(data.Length, 17))}");
            return true;
        }

        Helpers.Logger.WriteLine($"HidrawHelper: both SetFeature[{reportSize}] and Write[{reportSize}] failed for {path}: errno={Marshal.GetLastPInvokeError()}");
        return false;
    }

    /// <summary>
    /// Write multiple messages to every ASUS hidraw device whose product ID
    /// matches one of <paramref name="pids"/>. Used by the XG Mobile
    /// transport (PID set: see <c>XGM.XGM_PIDS</c>) which uses a
    /// 300-byte feature report on report id 0x5E. The <paramref name="reportSize"/>
    /// parameter lets callers pick the correct padding (default 64 = AURA).
    /// </summary>
    public static bool WriteAllForPids(
        byte reportId,
        IEnumerable<byte[]> messages,
        int[] pids,
        int reportSize = 64,
        string? log = null)
    {
        // Serialize with all other ASUS HID traffic (upstream #5784)
        lock (AsusHid.HidLock)
            return WriteAllForPidsLocked(reportId, messages, pids, reportSize, log);
    }

    private static bool WriteAllForPidsLocked(
        byte reportId,
        IEnumerable<byte[]> messages,
        int[] pids,
        int reportSize,
        string? log)
    {
        var pidSet = new HashSet<int>(pids);
        var paths = EnumerateAsusDevices()
            .Where(d => pidSet.Contains(d.Product))
            .Select(d => d.Path)
            .ToList();

        if (paths.Count == 0)
            return false;

        bool anySuccess = false;
        foreach (var path in paths)
        {
            int fd = -1;
            try
            {
                fd = open(path, O_RDWR);
                if (fd < 0)
                {
                    Helpers.Logger.WriteLine($"HidrawHelper: cannot open {path}: errno={Marshal.GetLastPInvokeError()}");
                    continue;
                }

                foreach (var data in messages)
                {
                    if (WriteToFdSized(fd, data, reportSize, path, log))
                        anySuccess = true;
                }
            }
            catch (Exception ex)
            {
                Helpers.Logger.WriteLine($"HidrawHelper: error writing to {path}: {ex.Message}");
            }
            finally
            {
                if (fd >= 0)
                    close(fd);
            }
        }

        return anySuccess;
    }

    /// <summary>
    /// Single-message overload of <see cref="WriteAllForPids(byte, IEnumerable{byte[]}, int[], int, string)"/>.
    /// </summary>
    public static bool WriteAllForPids(
        byte reportId,
        byte[] data,
        int[] pids,
        int reportSize = 64,
        string? log = null)
    {
        return WriteAllForPids(reportId, new[] { data }, pids, reportSize, log);
    }

    /// <summary>
    /// Returns the first hidraw path whose product ID matches one of
    /// <paramref name="pids"/>, or null if none. Useful for callers that
    /// need to identify the specific device path (e.g. diagnostics).
    /// </summary>
    public static string? GetFirstPathForPids(int[] pids)
    {
        var pidSet = new HashSet<int>(pids);
        return EnumerateAsusDevices()
            .Where(d => pidSet.Contains(d.Product))
            .Select(d => d.Path)
            .FirstOrDefault();
    }
}
