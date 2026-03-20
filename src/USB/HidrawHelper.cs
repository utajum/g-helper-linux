using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace GHelper.Linux.USB;

/// <summary>
/// Native Linux hidraw device discovery and I/O.
///
/// HidSharpCore only discovers USB-HID devices (it calls
/// udev_device_get_parent_with_subsystem_devtype("usb","usb_device")
/// and discards anything without a USB parent). This means I2C-HID
/// devices — like the ASUS TUF FA608PP keyboard (ITE51368:00 0B05:19B6)
/// — are completely invisible.
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
    // ── ioctl constants (from linux/hidraw.h) ──

    // _IOR('H', 0x03, struct hidraw_devinfo)  — get bus/vendor/product
    private const uint HIDIOCGRAWINFO = 0x80084803;
    // _IOR('H', 0x01, int)  — get report descriptor size
    private const uint HIDIOCGRDESCSIZE = 0x80044801;
    // _IOR('H', 0x02, struct hidraw_report_descriptor)  — get report descriptor
    private const uint HIDIOCGRDESC = 0x90044802;

    // HIDIOCSFEATURE = _IOWR('H', 0x06, len)  — send SetFeature report
    // The size field is variable; we encode it at call time.
    private static uint HIDIOCSFEATURE(int size)
    {
        return (uint)(0xC0004806 | ((size & 0x3FFF) << 16));
    }

    // HIDIOCGFEATURE = _IOWR('H', 0x07, len)  — read GetFeature report
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

    // Known AURA-capable product IDs (same list as AsusHid.DeviceIds)
    private static readonly HashSet<ushort> AuraProductIds = new()
    {
        0x1A30, 0x1854, 0x1869, 0x1866, 0x19B6, 0x1822, 0x1837,
        0x184A, 0x183D, 0x8502, 0x1807, 0x17E0, 0x18C6, 0x1ABE,
        0x1B4C, 0x1B6E, 0x1B2C, 0x8854
    };

    // ── P/Invoke ──

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

    // ── Structs ──

    [StructLayout(LayoutKind.Sequential)]
    private struct HidrawDevinfo
    {
        public uint bustype;   // __u32
        public short vendor;   // __s16
        public short product;  // __s16
    }

    /// <summary>
    /// Matches kernel struct hidraw_report_descriptor { __u32 size; __u8 value[HID_MAX_DESCRIPTOR_SIZE]; }
    /// Must use [MarshalAs] for correct pinning/layout — a raw byte[] doesn't work
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

    // ── Cache ──

    private static List<HidrawDeviceInfo>? _cachedDevices;
    private static readonly object _cacheLock = new();

    /// <summary>
    /// Enumerate all ASUS hidraw devices on the system, regardless of bus type.
    /// Results are cached for the lifetime of the process.
    /// </summary>
    public static IReadOnlyList<HidrawDeviceInfo> EnumerateAsusDevices()
    {
        lock (_cacheLock)
        {
            if (_cachedDevices != null)
                return _cachedDevices;

            _cachedDevices = new List<HidrawDeviceInfo>();

            try
            {
                // Scan /dev/hidraw0, hidraw1, ... up to hidraw31
                for (int i = 0; i < 32; i++)
                {
                    string path = $"/dev/hidraw{i}";
                    if (!File.Exists(path)) continue;

                    var info = ProbeDevice(path);
                    if (info != null && info.Vendor == ASUS_VENDOR_ID)
                    {
                        _cachedDevices.Add(info);
                        Helpers.Logger.WriteLine(
                            $"HidrawHelper: found ASUS device {path} PID=0x{info.Product:X4} Bus={info.BusName} Aura={info.HasAuraReport}");
                    }
                }
            }
            catch (Exception ex)
            {
                Helpers.Logger.WriteLine($"HidrawHelper: enumeration error: {ex.Message}");
            }

            return _cachedDevices;
        }
    }

    /// <summary>
    /// Invalidate the cached device list (e.g., after udev event).
    /// </summary>
    public static void InvalidateCache()
    {
        lock (_cacheLock) { _cachedDevices = null; }
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
        var paths = GetAuraDevicePaths().ToList();
        if (paths.Count == 0) return false;

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
                if (fd >= 0) close(fd);
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
    ///   1. SetFeature [0x5D, 0x05, 0x20, 0x31, 0x00, 0x1A] — query command
    ///   2. GetFeature [0x5D, ...] — read back capability response
    /// Response bytes of interest:
    ///   [9]  = KBBackLightType (0=single, 1=minimal, 2=multi-zone, 3=per-key, 4=four-zone)
    ///   [10] = Keyboard version (> 0x22 enables extended fields [17]-[23])
    ///   [13] = Zone bitfield (bit0=Logo, bit1=Lightbar, bit4=VCut, bit5=Aero, bit6=Bump, bit7=Rearglow)
    ///   [17] = Model series (1=Strix, 2=Flow, 4=Zephyrus, 8=TUF, 0x10=SE, 0x20=Desktop)
    ///   [18]-[23] = LED counts per zone (Lightbar, Logo, Aero, VCut, Rearglow, Bump)
    /// </summary>
    public static byte[]? QueryAuraCapabilities()
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
            query[5] = 0x1A;
            int ret = ioctl(fd, HIDIOCSFEATURE(64), query);
            if (ret < 0)
            {
                Helpers.Logger.WriteLine($"AURA GetFeature: SetFeature(0x05) failed on {path}: errno={Marshal.GetLastPInvokeError()}");
                return null;
            }

            // Small delay for firmware to prepare response
            Thread.Sleep(50);

            // Read back via GetFeature — report ID must be pre-set in buffer
            byte[] response = new byte[64];
            response[0] = 0x5D;  // report ID
            ret = ioctl(fd, HIDIOCGFEATURE(64), response);
            if (ret < 0)
            {
                Helpers.Logger.WriteLine($"AURA GetFeature: GetFeature(0x5D) failed on {path}: errno={Marshal.GetLastPInvokeError()}");
                return null;
            }

            Helpers.Logger.WriteLine($"AURA GetFeature OK on {path}: {BitConverter.ToString(response, 0, Math.Min(ret, 32))}");
            return response;
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"AURA GetFeature: exception on {path}: {ex.Message}");
            return null;
        }
        finally
        {
            if (fd >= 0) close(fd);
        }
    }

    // ── Internal ──

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
                if (fd < 0) return null;
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
            if (fd >= 0) close(fd);
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
    /// USB devices are NOT given this fallback — descriptor must match.
    /// </summary>
    private static bool FallbackForI2c(ushort busType, ushort pid, string reason)
    {
        if (busType == BUS_I2C && AuraProductIds.Contains(pid))
        {
            Helpers.Logger.WriteLine(
                $"HidrawHelper: I2C fallback — assuming AURA support for known PID 0x{pid:X4} ({reason})");
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
        // Pad to 64 bytes (standard AURA feature report size)
        const int reportSize = 64;
        byte[] padded = new byte[reportSize];
        Array.Copy(data, padded, Math.Min(data.Length, reportSize));

        // Try SetFeature first (this is how AURA protocol works)
        int ret = ioctl(fd, HIDIOCSFEATURE(reportSize), padded);
        if (ret >= 0)
        {
            if (log != null)
                Helpers.Logger.WriteLine($"HidrawHelper SetFeature {log}: {BitConverter.ToString(data, 0, Math.Min(data.Length, 17))}");
            return true;
        }

        // Fall back to raw write
        nint written = write(fd, padded, reportSize);
        if (written >= 0)
        {
            if (log != null)
                Helpers.Logger.WriteLine($"HidrawHelper Write {log}: {BitConverter.ToString(data, 0, Math.Min(data.Length, 17))}");
            return true;
        }

        Helpers.Logger.WriteLine($"HidrawHelper: both SetFeature and Write failed for {path}: errno={Marshal.GetLastPInvokeError()}");
        return false;
    }
}
