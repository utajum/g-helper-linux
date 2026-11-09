// Tiny test framework. Each scenario is a static method returning bool;
// the runner orchestrates fresh state, prints results, and exits non-zero
// when any scenario fails. No xUnit, no MSTest - single binary, fast to
// run from CI or the dev loop.

using GHelper.Linux.Gpu;
using GHelper.Linux.Helpers;
using GHelper.Linux.Platform.Linux;

namespace GHelper.Linux.Tests;

public static class Harness
{
    public static int Passed;
    public static int Failed;
    public static readonly List<string> FailedNames = new();

    /// <summary>Per-test sandbox state. Disposed at scenario end.</summary>
    public sealed class Sandbox : IDisposable
    {
        public string TempRoot { get; }
        public string ConfigDir { get; }
        public FakeAsusWmi Wmi { get; }
        public FakePowerManager Power { get; }
        public FakeBlockOps BlockOps { get; }
        public GPUModeControl Controller { get; }
        /// <summary>Monitor plugged into a dGPU port.</summary>
        public bool ExternalDisplay;

        public Sandbox(string scenarioName)
        {
            // GHELPER_TEST_ROOT is consumed at static-ctor time of
            // GPUModeControl, so we cannot vary it per scenario. Place
            // all scenarios under a shared root that was set before the
            // first SUT touch (see Program.Main).
            string root = Environment.GetEnvironmentVariable("GHELPER_TEST_ROOT")
                ?? throw new InvalidOperationException("GHELPER_TEST_ROOT not set - Program.Main must set it before any GPUModeControl access");
            TempRoot = root;

            // Wipe every test path so each scenario starts clean.
            string etcGhelper = Path.Combine(TempRoot, "etc", "ghelper");
            string etcModprobe = Path.Combine(TempRoot, "etc", "modprobe.d");
            string etcUdev = Path.Combine(TempRoot, "etc", "udev", "rules.d");
            Directory.CreateDirectory(etcGhelper);
            Directory.CreateDirectory(etcModprobe);
            Directory.CreateDirectory(etcUdev);
            foreach (var d in new[] { etcGhelper, etcModprobe, etcUdev })
                foreach (var f in Directory.EnumerateFiles(d))
                    File.Delete(f);

            // Wipe simulated sysfs / module / proc trees consumed by the
            // LinuxAsusWmi GPU-panel-visibility probes so each scenario
            // starts with no NVIDIA device, no nvidia/nouveau module, and
            // no /proc/sys/kernel/osrelease hint. Tests opt in to each via
            // WriteFakeNvidiaPciDevice() / WriteFakeNvidiaModule() /
            // WriteFakeNvidiaModuleOnDisk(). Cache is invalidated below so
            // the probes re-read the fresh state.
            string sysBusPci = Path.Combine(TempRoot, "sys", "bus", "pci", "devices");
            string sysBusPciSlots = Path.Combine(TempRoot, "sys", "bus", "pci", "slots");
            string sysModule = Path.Combine(TempRoot, "sys", "module");
            string libModules = Path.Combine(TempRoot, "lib", "modules");
            string procKernel = Path.Combine(TempRoot, "proc", "sys", "kernel");
            foreach (var d in new[] { sysBusPci, sysBusPciSlots, sysModule, libModules, procKernel })
            {
                if (Directory.Exists(d))
                    Directory.Delete(d, recursive: true);
                Directory.CreateDirectory(d);
            }
            LinuxAsusWmi.InvalidateGpuPresenceCache();

            // Fresh per-scenario config directory.
            ConfigDir = Path.Combine(TempRoot, "config", scenarioName);
            if (Directory.Exists(ConfigDir)) Directory.Delete(ConfigDir, recursive: true);
            Directory.CreateDirectory(ConfigDir);
            AppConfig.ResetForTest(ConfigDir);

            Wmi = new FakeAsusWmi();
            Power = new FakePowerManager();
            BlockOps = new FakeBlockOps();
            // Always install the seam so no scenario can shell out to the
            // real privileged helper by accident.
            Gpu.Block.GpuBlockControl.TestOps = BlockOps;
            // Model the display topology too, or results would depend on
            // whatever monitors the machine running the tests has plugged in.
            ExternalDisplay = false;
            Gpu.Block.DgpuDisplay.TestOverride = () => ExternalDisplay;
            Controller = new GPUModeControl(Wmi, Power);
        }

        public void Dispose() { /* Per-scenario state is reset on next ctor */ }

        // Convenience helpers -------------------------------------------------

        public string ModprobePath => GPUModeControl.ModprobeBlockPath;
        public string UdevPath => GPUModeControl.UdevRemovePath;
        public string TriggerPath => GPUModeControl.TriggerPath;
        public string BackendPath => GPUModeControl.BackendPath;

        public string PersistentPath => GPUModeControl.PersistentTriggerPath;

        public bool ModprobePresent() => File.Exists(ModprobePath);
        public bool UdevPresent() => File.Exists(UdevPath);
        public bool TriggerPresent() => File.Exists(TriggerPath);
        public bool BackendPresent() => File.Exists(BackendPath);
        public bool PersistentPresent() => File.Exists(PersistentPath);
        public string TriggerContent() => File.Exists(TriggerPath) ? File.ReadAllText(TriggerPath).Trim() : "";
        public string BackendContent() => File.Exists(BackendPath) ? File.ReadAllText(BackendPath).Trim() : "";
        public string PersistentContent() => File.Exists(PersistentPath) ? File.ReadAllText(PersistentPath).Trim() : "";

        public void WriteBlockArtifacts()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ModprobePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(UdevPath)!);
            File.WriteAllText(ModprobePath, "# test fixture\n");
            File.WriteAllText(UdevPath, "# test fixture\n");
        }

        public void WriteTrigger(string mode)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TriggerPath)!);
            File.WriteAllText(TriggerPath, mode);
        }

        public void WriteBackend(string backend)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BackendPath)!);
            File.WriteAllText(BackendPath, backend);
        }

        // ---- LinuxAsusWmi.IsPciBackendUsable fixtures ---------------------
        //
        // The GPU-panel-visibility probes scan three independent disk
        // locations: PCI bus, /sys/module, and /lib/modules. Each scenario
        // explicitly composes the state it wants by calling the helpers
        // below. After every state change call InvalidateGpuPresenceCache
        // so the next probe reads fresh data.

        /// <summary>
        /// Place a fake NVIDIA VGA-class device under the sandbox PCI bus.
        /// Mirrors what /sys/bus/pci/devices/0000:01:00.0 looks like for a
        /// real RTX dGPU. The scan keys off vendor 0x10de and class 0x0300xx.
        /// </summary>
        public void WriteFakeNvidiaPciDevice()
        {
            string dev = Path.Combine(TempRoot, "sys", "bus", "pci", "devices", "0000:01:00.0");
            Directory.CreateDirectory(dev);
            File.WriteAllText(Path.Combine(dev, "vendor"), "0x10de\n");
            File.WriteAllText(Path.Combine(dev, "class"), "0x030000\n");
            LinuxAsusWmi.InvalidateGpuPresenceCache();
        }

        /// <summary>
        /// Simulate the post-Eco udev hot-remove state: no NVIDIA device
        /// under /sys/bus/pci/devices. This is the default sandbox state
        /// (the constructor wipes the tree) so call this only when you
        /// want to be explicit about it.
        /// </summary>
        public void RemoveFakeNvidiaPciDevice()
        {
            string dev = Path.Combine(TempRoot, "sys", "bus", "pci", "devices", "0000:01:00.0");
            if (Directory.Exists(dev))
                Directory.Delete(dev, recursive: true);
            LinuxAsusWmi.InvalidateGpuPresenceCache();
        }

        /// <summary>
        /// Simulate the nvidia kernel module being loaded right now
        /// (driver bound). The probe checks <c>/sys/module/nvidia</c> as
        /// a presence test.
        /// </summary>
        public void WriteFakeNvidiaModule()
        {
            Directory.CreateDirectory(Path.Combine(TempRoot, "sys", "module", "nvidia"));
            LinuxAsusWmi.InvalidateGpuPresenceCache();
        }

        /// <summary>
        /// Simulate the nvidia .ko being installed on disk but not yet
        /// loaded. The probe walks <c>/lib/modules/&lt;release&gt;</c>.
        /// </summary>
        public void WriteFakeNvidiaModuleOnDisk()
        {
            // Use a fixed release so we don't depend on the host kernel.
            string release = "test-kernel-1.0";
            string osreleaseDir = Path.Combine(TempRoot, "proc", "sys", "kernel");
            Directory.CreateDirectory(osreleaseDir);
            File.WriteAllText(Path.Combine(osreleaseDir, "osrelease"), release + "\n");
            string updates = Path.Combine(TempRoot, "lib", "modules", release, "updates");
            Directory.CreateDirectory(updates);
            File.WriteAllText(Path.Combine(updates, "nvidia.ko"), "fake module\n");
            LinuxAsusWmi.InvalidateGpuPresenceCache();
        }

        // ---- GPU topology fixtures (HasSecondGpu) -------------------------

        /// <summary>nouveau.ko on disk, the way every distro kernel ships
        /// it, including iGPU-only machines. Weak dGPU evidence by design.</summary>
        public void WriteFakeNouveauOnDisk()
        {
            string release = "test-kernel-1.0";
            string osreleaseDir = Path.Combine(TempRoot, "proc", "sys", "kernel");
            Directory.CreateDirectory(osreleaseDir);
            File.WriteAllText(Path.Combine(osreleaseDir, "osrelease"), release + "\n");
            string nouveau = Path.Combine(TempRoot, "lib", "modules", release,
                "kernel", "drivers", "gpu", "drm", "nouveau");
            Directory.CreateDirectory(nouveau);
            File.WriteAllText(Path.Combine(nouveau, "nouveau.ko.zst"), "fake module\n");
            LinuxAsusWmi.InvalidateGpuPresenceCache();
        }

        /// <summary>AMD iGPU: boot display, VGA class, vendor 0x1002.</summary>
        public void WriteFakeAmdIgpuDevice()
        {
            string dev = Path.Combine(TempRoot, "sys", "bus", "pci", "devices", "0000:e2:00.0");
            Directory.CreateDirectory(dev);
            File.WriteAllText(Path.Combine(dev, "vendor"), "0x1002\n");
            File.WriteAllText(Path.Combine(dev, "class"), "0x030000\n");
            File.WriteAllText(Path.Combine(dev, "boot_vga"), "1\n");
            LinuxAsusWmi.InvalidateGpuPresenceCache();
        }

        /// <summary>Second display-class function from a vendor the dGPU
        /// scan does not know (Intel Arc): only the class count sees it.</summary>
        public void WriteFakeIntelDisplayDevice()
        {
            string dev = Path.Combine(TempRoot, "sys", "bus", "pci", "devices", "0000:03:00.0");
            Directory.CreateDirectory(dev);
            File.WriteAllText(Path.Combine(dev, "vendor"), "0x8086\n");
            File.WriteAllText(Path.Combine(dev, "class"), "0x038000\n");
            File.WriteAllText(Path.Combine(dev, "boot_vga"), "0\n");
            LinuxAsusWmi.InvalidateGpuPresenceCache();
        }

        /// <summary>PCIe slot dir with an address file, as pciehp exposes.</summary>
        public void WriteFakeSlot(string slot, string address)
        {
            string dir = Path.Combine(TempRoot, "sys", "bus", "pci", "slots", slot);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "address"), address + "\n");
        }
    }

    public static void Scenario(string name, Action<Sandbox> body)
    {
        try
        {
            using var sb = new Sandbox(name);
            body(sb);
            Console.WriteLine($"  PASS  {name}");
            Passed++;
        }
        catch (AssertException ex)
        {
            Console.WriteLine($"  FAIL  {name}");
            Console.WriteLine($"        {ex.Message}");
            Failed++;
            FailedNames.Add(name);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ERROR {name}: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Failed++;
            FailedNames.Add(name);
        }
    }

    public static void Assert(bool cond, string message)
    {
        if (!cond) throw new AssertException(message);
    }

    public static void AssertEqual<T>(T expected, T actual, string what)
    {
        if (!Equals(expected, actual))
            throw new AssertException($"{what}: expected {expected}, got {actual}");
    }
}

public sealed class AssertException : Exception
{
    public AssertException(string message) : base(message) { }
}
