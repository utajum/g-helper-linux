using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GHelper.Linux.Helpers;
using GHelper.Linux.I18n;
using GHelper.Linux.Platform.Linux;

namespace GHelper.Linux.Install;

// Whole file is Linux-only; the Unix file-mode APIs are guarded at runtime.
#pragma warning disable CA1416

/// <summary>
/// Self-contained system provisioning. Everything install-local.sh copies onto
/// the machine is embedded in this binary (see GHelper.Linux.csproj) and can be
/// installed / repaired from the running app, without the shell installer.
///
/// Three entry points:
///   - <see cref="CheckAndPromptAtStartup"/>: runs every launch; if any managed
///     file is missing/outdated it shows a single popup and (on confirm) applies
///     all fixes behind ONE pkexec prompt.
///   - <see cref="PopulateIntegrityPanel"/> + <see cref="RunFixFromUiAsync"/>:
///     the on-demand integrity list (green ✓ / red ✗) hosted in the Updates window.
///   - <see cref="ApplyAsRoot"/>: the privileged half, re-executed via
///     `pkexec &lt;self&gt; --apply-system-files id1,id2,...` (dispatched in
///     ResourceExtractorCli before Avalonia starts). Touches no Avalonia types.
///
/// The ghelper binary itself and its /usr/local/bin symlink are deliberately
/// NOT managed here (a binary cannot embed itself, and dev deployments run from
/// arbitrary paths).
/// </summary>
public static partial class Installer
{
    // Status model
    public enum FileState
    {
        Ok,           // present and byte-identical (and right mode)
        Missing,      // not on disk
        Outdated,     // present but content differs from the embedded copy
        WrongPerms,   // content matches but permission bits differ
        Unknown,      // present but not verifiable from user space (e.g. 0440 sudoers)
        Unavailable,  // not embedded in this build (dev run) - cannot repair
        NotApplicable,// not relevant to this hardware (e.g. GPU boot service on an iGPU-only device)
        Disabled,     // file is OK but the systemd service is not enabled
    }

    public sealed class ManagedFile
    {
        public string Id = "";          // stable token used on the CLI + UI
        public string NameKey = "";     // i18n key for the human-readable name
        public string? Resource;        // embedded LogicalName, or null when Generate is set
        public string Dest = "";
        public UnixFileMode Mode;
        public bool RootRequired;       // dest needs root to write
        public bool RootOwned;          // chown root:root after writing
        public bool PresenceOnly;       // existence is enough; don't hash (dynamic content)
        public string[] IgnoreLinePrefixes = []; // lines to drop (by left-trimmed prefix) before comparing
        public Func<byte[]?>? Generate; // produce content in-process (sudoers rule, filtered udev rules); null = resource unavailable
        public string[] Post = [];      // post-action tokens, run once after a root batch
        public Func<bool>? AppliesWhen; // null = always applies; else gate on hardware capability

        public bool Applies() => AppliesWhen?.Invoke() ?? true;
    }

    public sealed class FileResult
    {
        public ManagedFile File = null!;
        public FileState State;
    }

    // Permission constants
    private const UnixFileMode M755 =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    private const UnixFileMode M644 =
        UnixFileMode.UserRead | UnixFileMode.UserWrite |
        UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode M440 =
        UnixFileMode.UserRead | UnixFileMode.GroupRead;

    private const int PermMask = 0x1FF; // low 9 bits (rwxrwxrwx)
    private const int PkexecCancelled = -2;
    private const int PkexecNoAgent = -3;

    // Set when pkexec failed because no polkit authentication agent is
    // running in the session (Hyprland/sway without an agent: exit 127,
    // "Not authorized", no dialog ever shown - issue #143). Appended to the
    // failure message as a technical hint.
    private static string? _lastAuthHint;

    // comm values (15-char kernel truncation) of known polkit agents plus
    // the shells that embed one.
    private static readonly string[] PolkitAgentComms =
    [
        "polkit-gnome-au", "polkit-kde-auth", "polkit-mate-aut",
        "lxpolkit", "lxqt-policykit", "xfce-polkit", "hyprpolkitagent",
        "pantheon-agent", "dde-polkit-agen", "cosmic-osd", "gnome-shell",
    ];

    private static bool PolkitAgentPresent()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                string name = Path.GetFileName(dir);
                if (name.Length == 0 || name[0] < '0' || name[0] > '9')
                    continue;
                string comm;
                try
                { comm = File.ReadAllText(Path.Combine(dir, "comm")).Trim(); }
                catch
                { continue; }
                foreach (var agent in PolkitAgentComms)
                    if (comm.StartsWith(agent, StringComparison.Ordinal))
                        return true;
            }
        }
        catch { }
        return false;
    }

    // Post-action tokens
    private const string PostUdev = "udev";
    private const string PostSystemd = "systemd";
    private const string PostVisudo = "visudo";
    private const string PostDesktopDb = "desktopdb";
    private const string PostIconCache = "iconcache";
    private const string PostModprobe = "modprobe";
    private const string PostInputGroup = "inputgroup";

    // uinput is loaded everywhere (FN-Lock remapper, NumberPad, OSK).
    // i2c-dev is added only where the NumberPad can use it (see
    // ModulesForThisMachine).
    private static readonly string[] CommonModules = ["uinput"];

    // Lenovo-only modules (ideapad-laptop + WMI stack for profiles/PPT)
    private static readonly string[] LenovoModules =
        ["ideapad_laptop", "lenovo_wmi_gamezone", "lenovo_wmi_other", "lenovo_wmi_hotkey_utilities"];

    // Legacy modules-load.d path superseded by the unified ghelper.conf
    private const string LegacyModulesLoadPath = "/etc/modules-load.d/ghelper-lenovo.conf";

    // Legacy sudoers path. sudoers.d is read in lexical order and the LAST
    // matching rule wins; "ghelper-gpu" sorted before SteamOS's "wheel"
    // (%wheel ALL=(ALL) ALL), which silently re-required a password.
    private const string LegacySudoersPath = "/etc/sudoers.d/ghelper-gpu";

    // TODO needs to be removed at some point. Helpers formerly installed in the user-owned /opt/ghelper (NOPASSWD root escalation).
    private static readonly Dictionary<string, string> LegacyHelperPaths = new()
    {
        ["gpu_helper"] = "/opt/ghelper/gpu-helper",
        ["ryzenadj"] = "/opt/ghelper/ryzenadj",
    };

    // TODO needs to be removed at some point. sudoers last: it must name helpers already in place.
    private static readonly string[] HelperPathGroup = ["gpu_helper", "ryzenadj", "gpu_boot_script", "sudoers"];

    private const string BootService = "ghelper-gpu-boot.service";
    private const string HicolorDir = "/usr/share/icons/hicolor";
    private const string NvidiaVulkanIcd = "/usr/share/vulkan/icd.d/nvidia_icd.json";
    private static readonly string[] NvidiaEglVendors = new[]
    {
        "/usr/share/glvnd/egl_vendor.d/10_nvidia.json",
        "/usr/share/glvnd/egl_vendor.d/10_nvidia_wayland.json",
    };

    private static ManagedFile[]? _manifest;

    /// <summary>The ordered set of files this binary can install/repair.</summary>
    public static ManagedFile[] Manifest => _manifest ??= BuildManifest();

    private static ManagedFile[] BuildManifest()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string autostart = Path.Combine(home, ".config", "autostart", "ghelper.desktop");

        return
        [
            new ManagedFile
            {
                Id = "gpu_helper", NameKey = "sysfiles_name_gpu_helper",
                Resource = "gpu-helper", Dest = GpuHelperDest,
                Mode = M755, RootRequired = true, RootOwned = true,
            },
            new ManagedFile
            {
                // Upstream RyzenAdj CLI (static build, vendored from GitHub
                // releases). SMU power/temp/current tuning on AMD Ryzen APUs;
                // NotApplicable elsewhere so non-Ryzen machines never see it
                // in the install/update prompts.
                Id = "ryzenadj", NameKey = "sysfiles_name_ryzenadj",
                Resource = "ryzenadj", Dest = RyzenadjDest,
                Mode = M755, RootRequired = true, RootOwned = true,
                AppliesWhen = RyzenApplies,
            },
            new ManagedFile
            {
                // GPU eco/block helper only matters on hybrid machines; on
                // iGPU-only handhelds it must not show as Missing (and SteamOS
                // cannot even write /usr/local/lib while readonly is on).
                Id = "gpu_block_helper", NameKey = "sysfiles_name_gpu_block_helper",
                Resource = "gpu-block-helper.sh",
                Dest = "/usr/local/lib/ghelper/gpu-block-helper.sh",
                Mode = M755, RootRequired = true, RootOwned = true,
                AppliesWhen = BootGpuApplies,
            },
            new ManagedFile
            {
                Id = "gpu_boot_script", NameKey = "sysfiles_name_gpu_boot_script",
                Resource = "ghelper-gpu-boot.sh",
                Dest = "/usr/local/lib/ghelper/ghelper-gpu-boot.sh",
                Mode = M755, RootRequired = true, RootOwned = true,
                AppliesWhen = BootGpuApplies,
            },
            new ManagedFile
            {
                Id = "gpu_boot_service", NameKey = "sysfiles_name_gpu_boot_service",
                Resource = "ghelper-gpu-boot.service",
                Dest = "/etc/systemd/system/ghelper-gpu-boot.service",
                Mode = M644, RootRequired = true, RootOwned = true,
                Post = [PostSystemd], AppliesWhen = BootGpuApplies,
            },
            new ManagedFile
            {
                // Written as a per-machine filtered copy of the embedded
                // template: vendor blocks (asus / lenovo) and the NumberPad
                // i2c block are included only where the hardware exists, and
                // the world-writable compat block only when the
                // "udev_0666_fallback" config asks for it.
                Id = "udev_rules", NameKey = "sysfiles_name_udev_rules",
                Resource = "90-ghelper.rules",
                Generate = UdevRulesContent,
                Dest = "/etc/udev/rules.d/90-ghelper.rules",
                Mode = M644, RootRequired = true, RootOwned = true,
                IgnoreLinePrefixes = ["# Version:"], Post = [PostUdev, PostInputGroup],
            },
            new ManagedFile
            {
                // "zz-" prefix: must sort after distro group rules (e.g.
                // SteamOS "wheel") or their PASSWD grant overrides ours.
                Id = "sudoers", NameKey = "sysfiles_name_sudoers",
                Generate = SudoersContent,
                Dest = "/etc/sudoers.d/zz-ghelper-gpu",
                Mode = M440, RootRequired = true, RootOwned = true,
                Post = [PostVisudo],
            },
            new ManagedFile
            {
                Id = "desktop", NameKey = "sysfiles_name_desktop",
                Resource = "ghelper.desktop",
                Dest = ImmutableOs.IsImmutable
                    ? ImmutableOs.UserDesktopPath()
                    : "/usr/share/applications/ghelper.desktop",
                Mode = M644,
                RootRequired = !ImmutableOs.IsImmutable,
                RootOwned = !ImmutableOs.IsImmutable,
                Post = [PostDesktopDb],
            },
            new ManagedFile
            {
                Id = "icon", NameKey = "sysfiles_name_icon",
                Resource = "ghelper.png",
                Dest = ImmutableOs.IsImmutable
                    ? ImmutableOs.UserIconPath()
                    : "/usr/share/icons/hicolor/256x256/apps/ghelper.png",
                Mode = M644,
                RootRequired = !ImmutableOs.IsImmutable,
                RootOwned = !ImmutableOs.IsImmutable,
                Post = [PostIconCache],
            },
            new ManagedFile
            {
                Id = "autostart", NameKey = "sysfiles_name_autostart",
                Resource = "ghelper.desktop", Dest = autostart,
                Mode = M644, RootRequired = false, PresenceOnly = true,
            },
            new ManagedFile
            {
                // Unified modules-load.d config for all vendors.
                // Always includes uinput + i2c-dev (NumberPad virtual keyboard
                // and LED control). On Lenovo, also includes ideapad-laptop and
                // the lenovo-wmi stack for profiles + PPT.
                Id = "kernel_modules", NameKey = "sysfiles_name_kernel_modules",
                Generate = KernelModulesContent,
                Dest = "/etc/modules-load.d/ghelper.conf",
                Mode = M644, RootRequired = true, RootOwned = true,
                Post = [PostModprobe],
            },
        ];
    }

    /// <summary>Kernel modules this machine actually needs. Off-mode
    /// (default) matches install-local.sh: uinput + i2c-dev + Lenovo
    /// modules on Lenovo. Opt-in mode drops i2c-dev on hardware that
    /// cannot use it (non-ASUS or ASUS without NumberPad).</summary>
    private static List<string> ModulesForThisMachine()
    {
        var modules = new List<string>(CommonModules);
        bool per = UdevPerMachineMode;
        if (!per || (Helpers.AppConfig.IsAsusDevice() && NumberPadHardwarePresent()))
            modules.Add("i2c-dev");
        if (Helpers.AppConfig.IsLenovoDevice())
            modules.AddRange(LenovoModules);
        return modules;
    }

    private static byte[] KernelModulesContent()
    {
        return System.Text.Encoding.UTF8.GetBytes(
            "# Kernel modules required by G-Helper\n"
            + string.Join('\n', ModulesForThisMachine()) + "\n");
    }

    /// <summary>NumberPad-capable touchpad present (probe finds the device,
    /// regardless of whether i2c/uinput access currently works).</summary>
    private static bool NumberPadHardwarePresent()
    {
        try
        {
            return GHelper.Linux.Input.NumberPad.Probe().Status
                != GHelper.Linux.Input.NumberPad.ProbeStatus.NoHardware;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Set by --udev-compat in the pkexec re-exec: the root process
    /// cannot see the invoking user's config, so the flag travels on the CLI.</summary>
    private static bool _udevCompatOverride;

    /// <summary>Set by --udev-per-machine in the pkexec re-exec.</summary>
    private static bool _udevPerMachineOverride;

    private static bool UdevCompatMode =>
        _udevCompatOverride || Helpers.AppConfig.Is("udev_0666_fallback");

    /// <summary>Opt-in per-machine tailoring: filter udev rules by hardware
    /// and vendor, load only the kernel modules this machine can use, and
    /// tighten uinput/i2c to uaccess + input-group 0660. Default off keeps
    /// byte-identical output to install-local.sh so the app and the shell
    /// installer converge.</summary>
    private static bool UdevPerMachineMode =>
        _udevPerMachineOverride || Helpers.AppConfig.Is("udev_per_machine");

    /// <summary>
    /// Udev rules for this install. Off-mode (default) writes the full
    /// embedded template, marker comments stripped, so plain-copy script
    /// installs and app installs converge. Opt-in mode filters by
    /// #@section: universal / uinput / peripherals always; asus / lenovo
    /// by vendor; numberpad only on ASUS with the touchpad present;
    /// compat0666 (world-writable uinput+i2c) only when
    /// <see cref="UdevCompatMode"/>.
    /// </summary>
    private static byte[]? UdevRulesContent()
    {
        var raw = GetEmbedded("90-ghelper.rules");
        if (raw == null)
            return null;

        HashSet<string>? include = null;
        if (UdevPerMachineMode)
        {
            include = new HashSet<string> { "universal", "uinput", "peripherals" };
            if (Helpers.AppConfig.IsAsusDevice())
            {
                include.Add("asus");
                if (NumberPadHardwarePresent())
                    include.Add("numberpad");
            }
            if (Helpers.AppConfig.IsLenovoDevice())
                include.Add("lenovo");
            if (UdevCompatMode)
                include.Add("compat0666");
        }

        var kept = new List<string>();
        string section = "universal"; // header lines before the first marker
        foreach (var line in Encoding.UTF8.GetString(raw).Split('\n'))
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("#@section ", StringComparison.Ordinal))
            {
                section = trimmed["#@section ".Length..].Trim();
                continue;
            }
            // include == null in off-mode: keep every line (full template).
            if (include == null || include.Contains(section))
                kept.Add(line);
        }
        return Encoding.UTF8.GetBytes(string.Join("\n", kept));
    }

    /// <summary>Install location of the general privileged helper. Sealed
    /// SteamOS relocates it to the writable /etc overlay.</summary>
    internal static string GpuHelperDest => SysfsHelper.DefaultGpuHelperInstallPath;

    internal static string RyzenadjDest => SysfsHelper.DefaultRyzenadjInstallPath;

    /// <summary>ryzenadj is only useful on AMD Ryzen APUs; on other CPUs the
    /// binary and its sudoers line are NotApplicable (never advertised in the
    /// startup prompt or the Updates integrity panel).</summary>
    private static bool RyzenApplies() => Platform.Linux.RyzenPower.IsRyzenCpu;

    /// <summary>
    /// sudoers rule matching install-local.sh's (echo adds the trailing
    /// newline). References the root-owned helpers that are also managed
    /// here. The gpu-block-helper line is only emitted where the GPU boot
    /// integration applies (dual-GPU machines): whitelisting a path that is
    /// NotApplicable on this hardware would be inconsistent with the
    /// manifest. The grant is ALL (not just the installing user) because
    /// pkexec / other local admins invoke the helpers through the same rule.
    /// </summary>
    private static byte[] SudoersContent()
    {
        var sb = new StringBuilder();
        sb.Append("# G-Helper: passwordless access to the root-owned helper binaries\n");
        if (BootGpuApplies())
            sb.Append("ALL ALL=(root) NOPASSWD: /usr/local/lib/ghelper/gpu-block-helper.sh\n");
        sb.Append($"ALL ALL=(root) NOPASSWD: {GpuHelperDest}\n");
        if (RyzenApplies())
            sb.Append($"ALL ALL=(root) NOPASSWD: {RyzenadjDest}\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>The GPU-mode boot integration (ghelper-gpu-boot.sh/.service) only
    /// applies to machines that have a discrete GPU. On iGPU/APU-only hardware the
    /// boot service is a pure no-op, so it is treated as "not applicable": never
    /// flagged as missing, never auto-installed/enabled - but still shown (and
    /// removable) in the integrity panel. Uses a universal, Eco-resilient check.</summary>
    private static bool BootGpuApplies() => Gpu.GPUModeControl.HasDiscreteGpu();

    // Status computation (no UI, no privilege)
    public static List<FileResult> ComputeStatus()
    {
        var list = new List<FileResult>(Manifest.Length);
        foreach (var f in Manifest)
            list.Add(new FileResult { File = f, State = ComputeState(f) });
        return list;
    }

    public static FileState ComputeState(ManagedFile f)
    {
        // NixOS: files live at Nix-provided locations (PATH, system profile,
        // declarative udev/sudoers/modules) instead of the FHS paths. Verify
        // those instead of the hardcoded Dest so the panel reflects reality.
        if (Platform.Linux.NixOS.IsNixOS)
            return ComputeStateNixOS(f);

        // Capability-gated files (e.g. the GPU boot service on iGPU-only hardware)
        // are not relevant here: report Ok when present (so it can still be
        // Removed) or NotApplicable when absent - never Missing/Outdated, so it is
        // never flagged as a problem nor auto-installed by Install / Repair.
        if (!f.Applies())
            return File.Exists(f.Dest) ? FileState.Ok : FileState.NotApplicable;

        // The sudoers rule is 0440 root:root inside a 0750 dir, so it can't be
        // read (or even reliably stat'd) as a normal user. Verify it
        // functionally instead by inspecting the effective sudo policy.
        if (f.Id == "sudoers")
        {
            if (!File.Exists(f.Dest) && File.Exists(LegacySudoersPath))
                return FileState.Outdated;
            return ProbeSudoers();
        }

        // Autostart is owned by the app's own autostart toggle (AppConfig +
        // SetAutostart). If the user turned autostart off, the file's absence
        // is correct - never flag it (otherwise the popup would fight the
        // toggle and recreate the entry on every launch).
        if (f.Id == "autostart" && !AppConfig.IsNotFalse("autostart"))
            return FileState.Ok;

        byte[]? expected = ExpectedBytes(f);
        if (expected == null)
            return FileState.Unavailable; // resource missing from this build

        // The menu entry's Exec= is host-specific: compare against the resolved
        // launcher path (binary / AppImage) so a stale or placeholder Exec is
        // detected and repaired. Mirrors what the privileged writer substitutes.
        if (f.Id == "desktop")
            expected = SubstituteExec(expected, LinuxSystemIntegration.ResolveLauncherExecField());

        if (f.PresenceOnly)
            return File.Exists(f.Dest) ? FileState.Ok : FileState.Missing;

        if (!File.Exists(f.Dest))
            return FileState.Missing;

        if (f.RootOwned && !IsRootOwned(f.Dest))
            return FileState.WrongPerms;

        byte[] disk;
        try
        {
            disk = File.ReadAllBytes(f.Dest);
        }
        catch (UnauthorizedAccessException)
        {
            return FileState.Unknown; // present but not readable as this user
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Installer: read {f.Dest} failed: {ex.Message}");
            return FileState.Unknown;
        }

        var a = StripIgnored(expected, f.IgnoreLinePrefixes);
        var b = StripIgnored(disk, f.IgnoreLinePrefixes);
        if (!a.AsSpan().SequenceEqual(b))
            return FileState.Outdated;

        try
        {
            int actual = (int)File.GetUnixFileMode(f.Dest) & PermMask;
            int want = (int)f.Mode & PermMask;
            if (actual != want)
                return FileState.WrongPerms;
        }
        catch { /* stat failed; content already matched */ }

        if (f.Id == "gpu_boot_service"
            && !File.Exists($"/etc/systemd/system/multi-user.target.wants/{BootService}"))
            return FileState.Disabled;

        return FileState.Ok;
    }

    /// <summary>
    /// Verify a managed file against its Nix-provided location. On NixOS the
    /// nixos/ module + package supply these via PATH, the system profile, and
    /// declarative udev/sudoers/kernel-modules - not the FHS Dest paths. Each
    /// dependency is checked where Nix actually puts it so the integrity panel
    /// shows it as working (Ok) rather than missing or "not applicable".
    /// </summary>
    private static FileState ComputeStateNixOS(ManagedFile f)
    {
        switch (f.Id)
        {
            case "gpu_helper":
                return Platform.Linux.NixOS.ResolveGpuHelper() != null
                    ? FileState.Ok : FileState.Missing;

            case "gpu_block_helper":
                return Platform.Linux.NixOS.ResolveGpuBlockHelper() != null
                    ? FileState.Ok : FileState.Missing;

            case "ryzenadj":
                // nixpkgs package, added to PATH by the module.
                if (!f.Applies())
                    return FileState.NotApplicable;
                return Platform.Linux.NixOS.ResolveRyzenadj() != null
                    ? FileState.Ok : FileState.Missing;

            case "udev_rules":
                // Module ships these via services.udev.packages (symlink into
                // /etc/udev/rules.d from the read-only store).
                return File.Exists(Platform.Linux.NixOS.UdevRulePath)
                    ? FileState.Ok : FileState.Missing;

            case "sudoers":
                return ProbeSudoers();

            case "kernel_modules":
                return Platform.Linux.NixOS.KernelModulesLoaded()
                    ? FileState.Ok : FileState.Missing;

            case "desktop":
                return Platform.Linux.NixOS.DesktopFilePath() != null
                    ? FileState.Ok : FileState.Missing;

            case "icon":
                return Platform.Linux.NixOS.IconFilePath() != null
                    ? FileState.Ok : FileState.Missing;

            case "autostart":
                // User-writable (~/.config/autostart); the app manages it itself.
                if (!AppConfig.IsNotFalse("autostart"))
                    return FileState.Ok;
                return File.Exists(f.Dest) ? FileState.Ok : FileState.Missing;

            case "gpu_boot_script":
            case "gpu_boot_service":
                // Optional early-boot GPU service (module option, dGPU only).
                if (!f.Applies())
                    return FileState.NotApplicable;
                return File.Exists($"/etc/systemd/system/{BootService}")
                    || File.Exists($"/etc/systemd/system/multi-user.target.wants/{BootService}")
                    ? FileState.Ok : FileState.NotApplicable;

            default:
                return FileState.NotApplicable;
        }
    }

    /// <summary>
    /// The location to show for a managed file in the integrity panel. On
    /// NixOS the files live at Nix-provided paths (PATH, system profile) or
    /// are declarative (no file), so the hardcoded FHS Dest would be
    /// misleading. Returns the real path, or a short label for declarative
    /// items. Non-NixOS callers use f.Dest directly.
    /// </summary>
    private static string DisplayPathNixOS(ManagedFile f) => f.Id switch
    {
        "gpu_helper" => Platform.Linux.NixOS.ResolveGpuHelper() ?? f.Dest,
        "gpu_block_helper" => Platform.Linux.NixOS.ResolveGpuBlockHelper() ?? f.Dest,
        "ryzenadj" => Platform.Linux.NixOS.ResolveRyzenadj() ?? f.Dest,
        "desktop" => Platform.Linux.NixOS.DesktopFilePath() ?? f.Dest,
        "icon" => Platform.Linux.NixOS.IconFilePath() ?? f.Dest,
        "udev_rules" => f.Dest,   // real (store symlink into /etc)
        "autostart" => f.Dest,   // real (user dir)
        "sudoers" => "security.sudo.extraRules (declarative)",
        "kernel_modules" => "boot.kernelModules (uinput, i2c-dev)",
        "gpu_boot_script" or "gpu_boot_service" =>
            File.Exists($"/etc/systemd/system/{BootService}")
                ? $"/etc/systemd/system/{BootService}"
                : "module: services.ghelper.gpuBootService",
        _ => f.Dest,
    };

    /// <summary>
    /// Verify the passwordless sudoers rule by inspecting the effective sudo
    /// policy rather than reading the root-only file. Runs <c>sudo -n -l</c>
    /// (no command argument), which prints every rule that applies to the user,
    /// then checks that BOTH helper binaries have an active NOPASSWD entry.
    ///
    /// Why not <c>sudo -n -l &lt;cmd&gt;</c>: that exits 0 whenever the user may
    /// run the command by ANY rule (e.g. a blanket <c>(ALL) ALL</c> that
    /// sudo-group members get), so it cannot tell whether OUR NOPASSWD rule is in
    /// effect. Instead the listing is parsed with last-match-wins semantics
    /// (see <see cref="HasNopasswd"/>), which also detects a later blanket
    /// PASSWD rule overriding our grant. Immune to cached credentials - the
    /// listing reflects policy, not what the user can run right now.
    ///
    /// <c>-n</c> never prompts. Returns:
    ///   - <see cref="FileState.Ok"/> when both paths are listed NOPASSWD,
    ///   - <see cref="FileState.Missing"/> when the listing succeeds but a rule is
    ///     absent (commented out / file deleted),
    ///   - <see cref="FileState.Unknown"/> ("Not verified") when the policy cannot
    ///     be listed non-interactively (no cached timestamp and no NOPASSWD entry
    ///     to satisfy listpw), sudo is missing, or the probe times out.
    /// Does NOT execute the helpers - <c>-l</c> only reports policy.
    /// </summary>
    private static FileState ProbeSudoers()
    {
        string listing;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = SysfsHelper.SudoPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("-l");

            using var p = Process.Start(psi);
            if (p == null)
                return FileState.Unknown;

            // Drain stdout before WaitForExit (output is small; avoids a pipe
            // deadlock and gives us the policy listing to parse).
            listing = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(3000))
            {
                try
                { p.Kill(); }
                catch { }
                return FileState.Unknown;
            }
            // Non-zero means the policy could not be listed without a password
            // (-n refused to prompt): we cannot verify, so report "Not verified".
            if (p.ExitCode != 0)
                return FileState.Unknown;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Installer: sudoers probe failed: {ex.Message}");
            return FileState.Unknown;
        }

        string gpuHelperPath = GpuHelperDest;
        string blockHelperPath = "/usr/local/lib/ghelper/gpu-block-helper.sh";
        string ryzenadjPath = RyzenadjDest;

        // NixOS: the sudoers rule references the nix store paths. The resolvers
        // already return the store path (symlink followed), which is what the
        // app invokes and what the NOPASSWD rule grants - match against those.
        if (Platform.Linux.NixOS.IsNixOS)
        {
            gpuHelperPath = Platform.Linux.NixOS.ResolveGpuHelper() ?? gpuHelperPath;
            blockHelperPath = Platform.Linux.NixOS.ResolveGpuBlockHelper() ?? blockHelperPath;
            ryzenadjPath = Platform.Linux.NixOS.ResolveRyzenadj() ?? ryzenadjPath;
        }

        bool gpuHelper = HasNopasswd(listing, gpuHelperPath);
        // The block helper only matters where the GPU boot integration
        // applies; its sudoers line is not emitted elsewhere. Same for
        // ryzenadj on non-Ryzen CPUs.
        bool blockHelper = !BootGpuApplies() || HasNopasswd(listing, blockHelperPath);
        bool ryzenadj = !RyzenApplies() || HasNopasswd(listing, ryzenadjPath);
        return gpuHelper && blockHelper && ryzenadj ? FileState.Ok : FileState.Missing;
    }

    /// <summary>True if the sudo policy listing grants passwordless access to
    /// <paramref name="path"/> - i.e. some line names both NOPASSWD and the path
    /// (sudo prints one rule per line, possibly with several commands).</summary>
    private static bool HasNopasswd(string listing, string path)
    {
        bool granted = false;
        foreach (var raw in listing.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith('('))
                continue; // headers / Defaults lines
            if (line.Contains(path, StringComparison.Ordinal) || IsBlanketAll(line))
                granted = line.Contains("NOPASSWD", StringComparison.Ordinal);
        }
        return granted;
    }

    /// <summary>Rule whose command spec is ALL, e.g. "(ALL) ALL" or
    /// "(ALL : ALL) NOPASSWD: ALL" - matches every command incl. ours.</summary>
    private static bool IsBlanketAll(string line)
    {
        int close = line.IndexOf(')');
        if (close < 0)
            return false;
        string cmds = line[(close + 1)..];
        foreach (var tag in new[] { "NOPASSWD:", "PASSWD:", "SETENV:", "NOSETENV:", "EXEC:", "NOEXEC:" })
            cmds = cmds.Replace(tag, "");
        return cmds.Trim() == "ALL";
    }

    /// <summary>uid==0 check via stat(1); .NET exposes no owner API. Errors
    /// return true so a missing stat never flags a false repair.</summary>
    private static bool IsRootOwned(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "stat",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("%u");
            psi.ArgumentList.Add(path);
            using var p = Process.Start(psi);
            if (p == null)
                return true;
            string owner = p.StandardOutput.ReadToEnd().Trim();
            if (!p.WaitForExit(3000) || p.ExitCode != 0)
                return true;
            return owner == "0";
        }
        catch
        {
            return true;
        }
    }

    private static byte[]? ExpectedBytes(ManagedFile f)
        => f.Generate?.Invoke() ?? GetEmbedded(f.Resource);

    internal static byte[]? GetEmbedded(string? resource)
    {
        if (string.IsNullOrEmpty(resource))
            return null;
        using var s = typeof(Installer).Assembly.GetManifestResourceStream(resource);
        if (s == null)
            return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Drop lines whose left-trimmed start matches any ignore prefix so
    /// host-specific content does not trip the byte comparison (e.g. the udev
    /// rule's release-stamped "# Version:" line).</summary>
    private static byte[] StripIgnored(byte[] data, string[] prefixes)
    {
        if (prefixes.Length == 0)
            return data;
        var kept = Encoding.UTF8.GetString(data).Split('\n')
            .Where(l => !prefixes.Any(p => l.TrimStart().StartsWith(p, StringComparison.Ordinal)));
        return Encoding.UTF8.GetBytes(string.Join("\n", kept));
    }

    /// <summary>Replace the .desktop <c>Exec=</c> line with the host-resolved
    /// launcher field (already quoted if needed); all other lines untouched. The
    /// menu entry and autostart entry both ship with a placeholder <c>Exec=ghelper</c>
    /// that we rewrite to the actual binary / AppImage path at install time.</summary>
    private static byte[] SubstituteExec(byte[] desktop, string execField)
    {
        var lines = Encoding.UTF8.GetString(desktop).Split('\n');
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].StartsWith("Exec=", StringComparison.Ordinal))
                lines[i] = "Exec=" + execField;
        return Encoding.UTF8.GetBytes(string.Join("\n", lines));
    }

    private static bool IsProblem(FileState s)
        => s is FileState.Missing or FileState.Outdated or FileState.WrongPerms or FileState.Disabled;

    /// <summary>True when this item is both broken and actionable. A root file
    /// whose state is "Not verified" (e.g. the unreadable sudoers rule) is
    /// included so the per-row buttons collectively match the global fix.</summary>
    private static bool IsRepairable(FileResult r)
        => IsProblem(r.State) || (r.State == FileState.Unknown && r.File.RootRequired);

    // Apply (user-side orchestration: one pkexec for everything root)
    public static Task<(bool ok, int changed, bool cancelled)> ApplyAsync()
        => ApplyFilesAsync(ComputeStatus().Where(IsRepairable).Select(r => r.File).ToList());

    /// <summary>Repair a single managed file. A root file goes through its own
    /// pkexec prompt; user-level files are written directly.</summary>
    public static Task<(bool ok, int changed, bool cancelled)> ApplyOneAsync(ManagedFile f)
        => ApplyFilesAsync([f]);

    private static async Task<(bool ok, int changed, bool cancelled)> ApplyFilesAsync(List<ManagedFile> todo)
    {
        if (todo.Count == 0)
            return (true, 0, false);

        _lastAuthHint = null;
        int changed = 0;

        // User-level files first - never needs a prompt.
        foreach (var f in todo.Where(f => !f.RootRequired))
            if (ApplyUserFile(f))
                changed++;

        // All root files in a single pkexec re-exec of ourselves. The desktop
        // Exec= line is host-specific and must be resolved here: pkexec strips the
        // environment, so $APPIMAGE is not visible to the privileged writer.
        var rootIds = todo.Where(f => f.RootRequired).Select(f => f.Id).ToArray();
        if (rootIds.Length > 0)
        {
            string desktopExec = LinuxSystemIntegration.ResolveLauncherExecField();
            int rc = await Task.Run(() => RunPkexecApply(rootIds, desktopExec));
            if (rc == PkexecCancelled)
                return (false, changed, true);
            if (rc != 0)
                return (false, changed, false);
            changed += rootIds.Length;
        }

        return (true, changed, false);
    }

    private static bool ApplyUserFile(ManagedFile f)
    {
        try
        {
            byte[]? data = ExpectedBytes(f);
            if (data == null)
                return false;
            // Resolve the host-specific Exec= here (unprivileged: $APPIMAGE is visible).
            if (f.Id == "autostart" || f.Id == "desktop")
                data = SubstituteExec(data, LinuxSystemIntegration.ResolveLauncherExecField());
            var dir = Path.GetDirectoryName(f.Dest);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllBytes(f.Dest, data);
            try
            { File.SetUnixFileMode(f.Dest, f.Mode); }
            catch { }
            Logger.WriteLine($"Installer: wrote user file {f.Dest}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Installer: user file {f.Dest} failed: {ex.Message}");
            return false;
        }
    }

    private static int RunPkexecApply(string[] ids, string desktopExec)
    {
        try
        {
            // Under an AppImage, ProcessPath is inside the per-user FUSE mount
            // which root cannot read; resolve a root-runnable copy (deleted on
            // dispose, after pkexec returns).
            using var self = LinuxSystemIntegration.ResolvePrivilegedSelf();
            var psi = new ProcessStartInfo
            {
                FileName = "pkexec",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(self.Path);
            psi.ArgumentList.Add("--apply-system-files");
            psi.ArgumentList.Add(string.Join(",", ids));
            if (!string.IsNullOrEmpty(desktopExec))
            {
                psi.ArgumentList.Add("--desktop-exec");
                psi.ArgumentList.Add(desktopExec);
            }
            // The root re-exec cannot read this user's config; carry the
            // udev choices on the CLI so both sides generate the same
            // rules content.
            if (Helpers.AppConfig.Is("udev_0666_fallback"))
                psi.ArgumentList.Add("--udev-compat");
            if (Helpers.AppConfig.Is("udev_per_machine"))
                psi.ArgumentList.Add("--udev-per-machine");

            using var p = Process.Start(psi);
            if (p == null)
                return -1;
            string outp = p.StandardOutput.ReadToEnd();
            string errp = p.StandardError.ReadToEnd();
            p.WaitForExit(180000);
            Logger.WriteLine($"Installer: pkexec apply exit={p.ExitCode}; out={outp.Trim()}; err={errp.Trim()}");
            if (p.ExitCode == 127 && errp.Contains("Not authorized", StringComparison.OrdinalIgnoreCase)
                && !PolkitAgentPresent())
            {
                _lastAuthHint = "no polkit agent; run: sudo "
                    + (Environment.ProcessPath ?? "ghelper")
                    + " --apply-system-files " + string.Join(",", ids);
                Logger.WriteLine($"Installer: {_lastAuthHint}");
                return PkexecNoAgent;
            }
            // 126 = user dismissed the auth dialog, 127 = auth failed / not authorized.
            if (p.ExitCode is 126 or 127)
                return PkexecCancelled;
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Installer: pkexec apply failed: {ex.Message}");
            return -1;
        }
    }

    // Remove (user-side orchestration: one pkexec for everything root)
    /// <summary>Remove every managed file - the in-app counterpart to the shell
    /// installer's --uninstall, minus the ghelper binary and its /usr/local/bin
    /// symlink (a running process cannot delete itself) and ~/.config/ghelper
    /// (user settings are preserved). Root files go through ONE pkexec prompt
    /// that also disables the boot service and restores the NVIDIA Vulkan ICD;
    /// user-level files are deleted directly.</summary>
    public static Task<(bool ok, int removed, bool cancelled)> RemoveAsync()
        => RemoveFilesAsync(Manifest.ToList());

    private static async Task<(bool ok, int removed, bool cancelled)> RemoveFilesAsync(List<ManagedFile> todo)
    {
        if (todo.Count == 0)
            return (true, 0, false);

        int removed = 0;

        // User-level files first - never needs a prompt.
        foreach (var f in todo.Where(f => !f.RootRequired))
            if (RemoveUserFile(f))
                removed++;

        // All root files in a single pkexec re-exec of ourselves.
        var rootIds = todo.Where(f => f.RootRequired).Select(f => f.Id).ToArray();
        if (rootIds.Length > 0)
        {
            int rc = await Task.Run(() => RunPkexecRemove(rootIds));
            if (rc == PkexecCancelled)
                return (false, removed, true);
            if (rc != 0)
                return (false, removed, false);
            removed += rootIds.Length;
        }

        return (true, removed, false);
    }

    private static bool RemoveUserFile(ManagedFile f)
    {
        try
        {
            if (!File.Exists(f.Dest))
                return false;
            File.Delete(f.Dest);
            Logger.WriteLine($"Installer: removed user file {f.Dest}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Installer: remove user file {f.Dest} failed: {ex.Message}");
            return false;
        }
    }

    private static int RunPkexecRemove(string[] ids)
    {
        try
        {
            // Under an AppImage, ProcessPath is inside the per-user FUSE mount
            // which root cannot read; resolve a root-runnable copy (deleted on
            // dispose, after pkexec returns).
            using var self = LinuxSystemIntegration.ResolvePrivilegedSelf();
            var psi = new ProcessStartInfo
            {
                FileName = "pkexec",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(self.Path);
            psi.ArgumentList.Add("--remove-system-files");
            psi.ArgumentList.Add(string.Join(",", ids));

            using var p = Process.Start(psi);
            if (p == null)
                return -1;
            string outp = p.StandardOutput.ReadToEnd();
            string errp = p.StandardError.ReadToEnd();
            p.WaitForExit(180000);
            Logger.WriteLine($"Installer: pkexec remove exit={p.ExitCode}; out={outp.Trim()}; err={errp.Trim()}");
            // 126 = user dismissed the auth dialog, 127 = auth failed / not authorized.
            if (p.ExitCode is 126 or 127)
                return PkexecCancelled;
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Installer: pkexec remove failed: {ex.Message}");
            return -1;
        }
    }

    // Privileged half (root) - invoked via pkexec, no Avalonia here
    public static int ApplyAsRoot(string[] args)
    {
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            Console.Error.WriteLine("usage: --apply-system-files <id1,id2,...>");
            return 1;
        }

        var ids = args[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Optional: the host-resolved .desktop Exec= field, passed by the
        // unprivileged caller (pkexec strips $APPIMAGE, so it can't be resolved here).
        string? desktopExec = null;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--desktop-exec" && i + 1 < args.Length)
                desktopExec = args[i + 1];
            if (args[i] == "--udev-compat")
                _udevCompatOverride = true;
            if (args[i] == "--udev-per-machine")
                _udevPerMachineOverride = true;
        }

        var byId = Manifest.ToDictionary(f => f.Id);
        var post = new HashSet<string>();
        int failures = 0;

        // TODO needs to be removed at some point. Files that name the helper paths must move together.
        if (LegacyHelperPaths.Values.Any(File.Exists) && ids.Any(HelperPathGroup.Contains))
            ids = [.. ids.Where(id => id != "sudoers").Union(HelperPathGroup.Where(id => byId[id].Applies()))];

        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var f))
            {
                Console.Error.WriteLine($"unknown id '{id}'");
                failures++;
                continue;
            }
            if (!f.RootRequired)
                continue; // user-level files are handled by the unprivileged process

            byte[]? data = ExpectedBytes(f);
            if (data == null)
            {
                Console.Error.WriteLine($"{id}: resource not embedded");
                failures++;
                continue;
            }

            if (f.Id == "desktop" && !string.IsNullOrEmpty(desktopExec))
                data = SubstituteExec(data, desktopExec);

            if (WriteRoot(f, data, out string err))
            {
                Console.WriteLine($"applied {id} -> {f.Dest}");
                if (LegacyHelperPaths.TryGetValue(id, out var legacy))
                    // TODO needs to be removed at some point
                    TryDelete(legacy);
                foreach (var p in f.Post)
                    post.Add(p);
            }
            else
            {
                Console.Error.WriteLine($"{id}: {err}");
                failures++;
            }
        }

        EnsureStateDir();
        RunPostActions(post);
        return failures == 0 ? 0 : 2;
    }

    /// <summary>Ensure the GPU-mode state dir exists (root:root 0755). The boot
    /// service declares <c>ReadWritePaths=/etc/ghelper</c>, which systemd requires
    /// to pre-exist to set up its namespace; create it for everyone so the unit
    /// can never fail on a missing dir. Harmless on machines that never switch GPU
    /// modes (the dir simply stays empty).</summary>
    private static void EnsureStateDir()
    {
        try
        {
            Directory.CreateDirectory("/etc/ghelper");
            File.SetUnixFileMode("/etc/ghelper",
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            Run("chown", "root:root", "/etc/ghelper");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ensure /etc/ghelper failed: {ex.Message}");
        }
    }

    /// <summary>Privileged removal half - invoked via
    /// `pkexec &lt;self&gt; --remove-system-files id1,id2,...`. Deletes the
    /// root-owned managed files, disables the boot service before its unit is
    /// removed, restores the NVIDIA Vulkan ICD, and reloads the affected daemons
    /// (without re-enabling anything). Touches no Avalonia types.</summary>
    public static int RemoveAsRoot(string[] args)
    {
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            Console.Error.WriteLine("usage: --remove-system-files <id1,id2,...>");
            return 1;
        }

        var ids = args[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var byId = Manifest.ToDictionary(f => f.Id);
        var post = new HashSet<string>();
        int failures = 0;

        // Disable the boot service BEFORE its unit file is deleted, so systemd is
        // not left with a dangling enablement symlink.
        if (ids.Contains("gpu_boot_service"))
            Run("systemctl", "disable", BootService);

        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var f))
            {
                Console.Error.WriteLine($"unknown id '{id}'");
                failures++;
                continue;
            }
            if (!f.RootRequired)
                continue; // user-level files are handled by the unprivileged process

            if (DeleteRoot(f, out string err))
            {
                Console.WriteLine($"removed {id} -> {f.Dest}");
                foreach (var p in f.Post)
                    post.Add(p);
            }
            else
            {
                Console.Error.WriteLine($"{id}: {err}");
                failures++;
            }
        }

        // Clean up legacy modules-load.d file superseded by ghelper.conf
        TryDelete(LegacyModulesLoadPath);
        TryDelete(LegacySudoersPath);
        foreach (var legacy in LegacyHelperPaths.Values)
            // TODO needs to be removed at some point
            TryDelete(legacy);

        RestoreVulkanIcd();
        RunRemovePostActions(post);
        return failures == 0 ? 0 : 2;
    }

    private static bool WriteRoot(ManagedFile f, byte[] data, out string err)
    {
        err = "";
        string tmp = f.Dest + ".ghnew";
        try
        {
            var dir = Path.GetDirectoryName(f.Dest);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllBytes(tmp, data);
            File.SetUnixFileMode(tmp, f.Mode);

            if (f.RootOwned && Run("chown", "root:root", tmp) != 0)
            {
                TryDelete(tmp);
                err = "chown root:root failed";
                return false;
            }

            // Validate sudoers BEFORE it goes live; a bad rule can lock out sudo.
            if (f.Post.Contains(PostVisudo) && Run("visudo", "-c", "-f", tmp) != 0)
            {
                TryDelete(tmp);
                err = "visudo validation failed";
                return false;
            }

            File.Move(tmp, f.Dest, overwrite: true); // atomic within the same dir
            return true;
        }
        catch (Exception ex)
        {
            TryDelete(tmp);
            err = ex.Message;
            return false;
        }
    }

    private static void RunPostActions(HashSet<string> post)
    {
        if (post.Contains(PostUdev))
        {
            Run("udevadm", "control", "--reload-rules");
            Run("udevadm", "trigger");
        }
        if (post.Contains(PostSystemd))
        {
            Run("systemctl", "daemon-reload");
            Run("systemctl", "enable", BootService);
        }
        if (post.Contains(PostDesktopDb))
            Run("update-desktop-database", "/usr/share/applications");
        if (post.Contains(PostIconCache))
            Run("gtk-update-icon-cache", "-f", "-t", HicolorDir);
        if (post.Contains(PostModprobe))
        {
            // Best effort: load each module separately so one missing module
            // (older kernel without lenovo-wmi) doesn't block the others.
            foreach (var module in ModulesForThisMachine())
                Run("modprobe", module);
        }
        if (post.Contains(PostInputGroup) && !UdevCompatMode)
        {
            // The uaccess/0660 uinput+i2c rules need the invoking user in the
            // "input" group on setups without logind seats. pkexec exports
            // the caller's uid; resolve it to a name and add the membership
            // (takes effect on next login; uaccess covers the current one).
            string? uidRaw = Environment.GetEnvironmentVariable("PKEXEC_UID")
                ?? Environment.GetEnvironmentVariable("SUDO_UID");
            if (int.TryParse(uidRaw, out int uid) && uid > 0)
                Run("sh", "-c",
                    $"u=$(getent passwd {uid} | cut -d: -f1); " +
                    "[ -n \"$u\" ] && getent group input >/dev/null && usermod -aG input \"$u\" || true");
        }

        // Clean up legacy modules-load.d file superseded by ghelper.conf
        TryDelete(LegacyModulesLoadPath);
        // Superseded by zz-ghelper-gpu (sorts after distro wheel rule)
        if (File.Exists(Manifest.First(f => f.Id == "sudoers").Dest))
            TryDelete(LegacySudoersPath);
        // PostVisudo is validated inline in WriteRoot.
    }

    private static bool DeleteRoot(ManagedFile f, out string err)
    {
        err = "";
        try
        {
            if (File.Exists(f.Dest))
                File.Delete(f.Dest);
            return true; // already-absent counts as removed
        }
        catch (Exception ex)
        {
            err = ex.Message;
            return false;
        }
    }

    /// <summary>Post-actions for removal: reload the daemons whose files were
    /// deleted, but never re-enable the (now removed) boot service. The service
    /// itself is disabled earlier, before its unit file is deleted.</summary>
    private static void RunRemovePostActions(HashSet<string> post)
    {
        if (post.Contains(PostUdev))
            Run("udevadm", "control", "--reload-rules");
        if (post.Contains(PostSystemd))
            Run("systemctl", "daemon-reload");
        if (post.Contains(PostDesktopDb))
            Run("update-desktop-database", "/usr/share/applications");
        if (post.Contains(PostIconCache))
            Run("gtk-update-icon-cache", "-f", "-t", HicolorDir);
    }

    /// <summary>If GPU Eco mode left the NVIDIA Vulkan ICD or EGL vendor hidden
    /// (renamed to .json_inactive), move them back so apps see the dGPU again
    /// once G-Helper is removed. Best-effort.</summary>
    private static void RestoreVulkanIcd()
    {
        RestoreOneIcd(NvidiaVulkanIcd);
        foreach (var egl in NvidiaEglVendors)
            RestoreOneIcd(egl);
    }

    private static void RestoreOneIcd(string path)
    {
        try
        {
            string inactive = path + "_inactive";
            if (File.Exists(inactive) && !File.Exists(path))
            {
                File.Move(inactive, path);
                Console.WriteLine($"restored {Path.GetFileName(path)}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ICD restore {path}: {ex.Message}");
        }
    }

    private static int Run(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null)
                return -1;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(120000);
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{file}: {ex.Message}");
            return -1;
        }
    }

    private static void TryDelete(string path)
    {
        try
        { File.Delete(path); }
        catch { }
    }

    // Startup privileged-decision gate
    //
    // The startup system-files prompt installs gpu-helper (among other files)
    // behind ONE pkexec. The GPU/CPU power-apply that also runs at launch reaches
    // gpu-helper through NvidiaProcessScanner.EnsureHelper, which would otherwise
    // fire its OWN competing `--install-gpu-helper` pkexec at the same moment -
    // two concurrent polkit prompts that fight each other (the user's reported
    // bug). This gate lets the background apply WAIT for the user's decision, and
    // (via StartupWillPrompt) suppress the redundant self-install entirely when
    // the prompt is the thing handling installation.
    private static readonly TaskCompletionSource<bool> _startupGate =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static volatile bool _startupWillPrompt;

    /// <summary>Completes once the startup system-files decision is made (prompt
    /// shown and resolved, or skipped because there was nothing to do / the user
    /// opted out). Privileged background ops await this before touching the
    /// helper.</summary>
    public static Task StartupDecision => _startupGate.Task;

    /// <summary>True when the startup prompt criteria were met this launch (the
    /// check is enabled AND at least one managed file needs attention). While
    /// true, the gpu-helper self-install pkexec is suppressed - the prompt owns
    /// installation. Set before the prompt is shown; safe to read across threads.</summary>
    public static bool StartupWillPrompt => _startupWillPrompt;

    /// <summary>Block the calling background thread until the startup system-files
    /// decision is made, so a privileged self-install doesn't race the prompt's
    /// own pkexec. No-op on the UI thread (the prompt is modal there - blocking
    /// would deadlock) and once the decision is already made. Bounded so a missed
    /// prompt can never wedge startup permanently.</summary>
    public static void WaitForStartupDecision()
    {
        if (_startupGate.Task.IsCompleted || Dispatcher.UIThread.CheckAccess())
            return;
        try
        {
            if (!_startupGate.Task.Wait(TimeSpan.FromMinutes(3)))
                Logger.WriteLine("Installer: startup decision wait timed out");
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Installer: startup decision wait failed: {ex.Message}");
        }
    }

    // UI: startup popup
    public static void CheckAndPromptAtStartup(Window? owner)
        => _ = CheckAndPromptAtStartupAsync(owner);

    private static async Task CheckAndPromptAtStartupAsync(Window? owner)
    {
        try
        {
            // ComputeStatus spawns `sudo -n -l` (up to 3 s) and hashes every
            // managed file: keep it off the UI thread. The await resumes on
            // the dispatcher, so the prompt below still runs on UI.
            var status = await Task.Run(ComputeStatus);
            LogStatus(status);

            // NixOS: dependencies are provided declaratively by the nixos/
            // module + package (verified above). /etc is read-only so the
            // pkexec self-install can't run - skip the prompt. The Updates
            // window integrity panel still shows real per-file status.
            if (Platform.Linux.NixOS.SkipSelfInstall)
            {
                Logger.WriteLine("Installer: NixOS - integration provided by module, skipping prompt");
                _startupGate.TrySetResult(true);
                return;
            }

            // User opted out of the startup check (Extra settings or the popup's
            // own "Don't show this again" box - both write sysfiles_skip_startup).
            if (AppConfig.Is("sysfiles_skip_startup"))
            {
                Logger.WriteLine("Installer: startup system-file check skipped (user setting)");
                _startupGate.TrySetResult(true);
                return;
            }
            var problems = status.Where(r => IsProblem(r.State)).ToList();
            if (problems.Count == 0)
            {
                Logger.WriteLine("Installer: all managed system files OK");
                _startupGate.TrySetResult(true);
                return;
            }
            Logger.WriteLine($"Installer: {problems.Count} system file(s) need attention - prompting");
            // Mark the suppression BEFORE the prompt is shown, so a background
            // helper install that is already waiting reads the right value even
            // if its bounded wait expires while the prompt is still open.
            _startupWillPrompt = true;
            _ = PromptAndApplyAsync(owner, problems);
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Installer.CheckAndPromptAtStartup failed: {ex.Message}");
            _startupGate.TrySetResult(true);
        }
    }

    private static async Task PromptAndApplyAsync(Window? owner, List<FileResult> problems)
    {
        try
        {
            if (!await ShowFixDialogAsync(owner, problems))
            {
                Logger.WriteLine("Installer: user declined system-file update");
                return;
            }
            var (ok, changed, cancelled) = await ApplyAsync();
            Logger.WriteLine($"Installer: startup apply ok={ok} changed={changed} cancelled={cancelled}");
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Installer.PromptAndApplyAsync failed: {ex.Message}");
        }
        finally
        {
            // Release the gate only once the user has resolved the prompt
            // (installed / declined / closed) - that is the "decision" the
            // background helper install was waiting for.
            _startupGate.TrySetResult(true);
        }
    }

    private static async Task<bool> ShowFixDialogAsync(Window? owner, List<FileResult> problems)
    {
        var tcs = new TaskCompletionSource<bool>();

        var dialog = new Window
        {
            Title = Labels.Get("sysfiles_popup_title"),
            Width = 460,
            MinWidth = 460,
            MaxWidth = 460,
            MaxHeight = 540,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.Manual,
            CanResize = false,
            WindowDecorations = WindowDecorations.Full,
            Background = new SolidColorBrush(Color.Parse("#1C1C1C")),
        };
        try
        { dialog.Icon = owner?.Icon; }
        catch { }

        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 16), Spacing = 10 };
        root.Children.Add(new TextBlock
        {
            Text = Labels.Get("sysfiles_popup_intro"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")),
        });

        var listPanel = new StackPanel { Spacing = 4 };
        foreach (var r in problems)
            listPanel.Children.Add(BuildRow(r, showPath: false));
        root.Children.Add(new ScrollViewer
        {
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = listPanel,
        });

        if (problems.Any(p => p.File.RootRequired))
            root.Children.Add(new TextBlock
            {
                Text = Labels.Get("sysfiles_popup_root_note"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#999999")),
            });

        // "Don't show this again" writes the same config key as the Extra
        // settings checkbox, so either entry point suppresses the startup popup.
        var dontShowAgain = new CheckBox
        {
            Content = Labels.Get("sysfiles_popup_dont_show_again"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")),
            IsChecked = AppConfig.Is("sysfiles_skip_startup"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        dontShowAgain.IsCheckedChanged += (_, _) =>
            AppConfig.Set("sysfiles_skip_startup", (dontShowAgain.IsChecked ?? false) ? 1 : 0);
        root.Children.Add(dontShowAgain);

        var btnConfirm = new Button
        {
            Content = Labels.Get("sysfiles_popup_confirm"),
            Classes = { "ghelper" },
            MinWidth = 140,
            Padding = new Thickness(14, 8),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        var btnCancel = new Button
        {
            Content = Labels.Get("sysfiles_popup_cancel"),
            MinWidth = 110,
            Padding = new Thickness(14, 8),
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        btnConfirm.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        btnCancel.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        btnRow.Children.Add(btnConfirm);
        btnRow.Children.Add(btnCancel);
        root.Children.Add(btnRow);

        dialog.Content = root;
        dialog.Closed += (_, _) =>
        {
            if (!tcs.Task.IsCompleted)
                tcs.TrySetResult(false);
        };

        // Center on the main window's screen (primary-screen fallback under
        // silent_start) instead of over the bottom-right main window, so the
        // startup prompt appears centered. Two-phase: estimate now, exact
        // reposition on Opened.
        WindowPositioner.CenterOfMainWindowOrPrimaryMonitor(dialog);

        // ShowDialog demands a visible owner; silent_start / --osk startups
        // keep the main window hidden, so fall back to a free-standing show.
        if (owner is { IsVisible: true })
            await dialog.ShowDialog(owner);
        else
            dialog.Show();

        return await tcs.Task;
    }

    // UI: integrity panel (hosted in the Updates window)
    /// <summary>Build the integrity list. When <paramref name="onRepair"/> is
    /// supplied, problem rows get a per-item Repair button that invokes it; when
    /// <paramref name="onRemove"/> is supplied, healthy (OK) rows get a per-item
    /// Remove button.</summary>
    public static void PopulateIntegrityPanel(Panel panel, Func<ManagedFile, Task>? onRepair = null, Func<ManagedFile, Task>? onRemove = null, Func<ManagedFile, Task>? onDiff = null)
        => PopulateIntegrityPanel(panel, ComputeStatus(), onRepair, onRemove, onDiff);

    /// <summary>Overload for callers that already computed the status on a
    /// background thread (ComputeStatus spawns a sudo probe).</summary>
    public static void PopulateIntegrityPanel(Panel panel, List<FileResult> results, Func<ManagedFile, Task>? onRepair = null, Func<ManagedFile, Task>? onRemove = null, Func<ManagedFile, Task>? onDiff = null)
    {
        panel.Children.Clear();
        foreach (var r in results)
            panel.Children.Add(BuildRow(r, showPath: true, onRepair, onRemove, onDiff));
    }

    /// <summary>Run the fix flow from the integrity panel's button; returns a
    /// localized result message for display.</summary>
    public static Task<string> RunFixFromUiAsync() => ResultMessageAsync(ApplyAsync());

    /// <summary>Repair one file from its per-row button; returns a localized
    /// result message for display.</summary>
    public static Task<string> RepairOneFromUiAsync(ManagedFile f) => ResultMessageAsync(ApplyOneAsync(f));

    private static async Task<string> ResultMessageAsync(Task<(bool ok, int changed, bool cancelled)> apply)
    {
        var (ok, changed, cancelled) = await apply;
        if (cancelled)
            return Labels.Get("sysfiles_auth_cancelled");
        if (!ok)
        {
            string msg = Labels.Get("sysfiles_apply_failed");
            if (_lastAuthHint != null)
                msg += "\n" + _lastAuthHint;
            return msg;
        }
        if (changed == 0)
            return Labels.Get("sysfiles_all_ok");
        return Labels.Format("sysfiles_applied", changed);
    }

    /// <summary>Run the uninstall flow from the Updates window's header button;
    /// returns a localized result message for display.</summary>
    public static Task<string> RunRemoveFromUiAsync() => RemoveResultMessageAsync(RemoveAsync());

    /// <summary>Remove a single managed file from its per-row button; returns a
    /// localized result message for display.</summary>
    public static Task<string> RemoveOneFromUiAsync(ManagedFile f) => RemoveResultMessageAsync(RemoveFilesAsync([f]));

    private static async Task<string> RemoveResultMessageAsync(Task<(bool ok, int removed, bool cancelled)> remove)
    {
        var (ok, _, cancelled) = await remove;
        if (cancelled)
            return Labels.Get("sysfiles_auth_cancelled");
        if (!ok)
            return Labels.Get("sysfiles_remove_failed");
        return Labels.Get("sysfiles_removed");
    }

    private static Control BuildRow(FileResult r, bool showPath, Func<ManagedFile, Task>? onRepair = null, Func<ManagedFile, Task>? onRemove = null, Func<ManagedFile, Task>? onDiff = null)
    {
        var (glyph, color) = GlyphFor(r.State);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"),
            Margin = new Thickness(0, 2, 0, 2),
        };

        var mark = new TextBlock
        {
            Text = glyph,
            Foreground = new SolidColorBrush(Color.Parse(color)),
            FontWeight = FontWeight.Bold,
            FontSize = 14,
            Width = 20,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        Grid.SetColumn(mark, 0);
        grid.Children.Add(mark);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        info.Children.Add(new TextBlock
        {
            Text = Labels.Get(r.File.NameKey),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#F0F0F0")),
        });
        if (showPath)
            info.Children.Add(new TextBlock
            {
                Text = Platform.Linux.NixOS.IsNixOS ? DisplayPathNixOS(r.File) : r.File.Dest,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.Parse("#888888")),
                FontFamily = new FontFamily("monospace"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var status = new TextBlock
        {
            Text = StatusText(r.State),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(color)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(status, 2);
        grid.Children.Add(status);

        // Per-row Diff button: only for Outdated rows (the only state with a
        // meaningful content difference), and only when the panel provided a diff
        // handler (the startup popup passes none, so it never appears there).
        if (onDiff != null && r.State == FileState.Outdated)
        {
            var diff = new Button
            {
                Content = Labels.Get("sysfiles_diff"),
                Classes = { "ghelper" },
                FontSize = 11,
                Padding = new Thickness(10, 3),
                Margin = new Thickness(8, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            diff.Click += async (_, _) => await onDiff(r.File);
            Grid.SetColumn(diff, 3);
            grid.Children.Add(diff);
        }

        // Per-row Repair button: only for actionable problem rows, and only when
        // the panel provided a repair handler (the startup popup passes none).
        if (onRepair != null && IsRepairable(r))
        {
            var repair = new Button
            {
                Content = Labels.Get("sysfiles_repair"),
                Classes = { "ghelper" },
                FontSize = 11,
                Padding = new Thickness(10, 3),
                Margin = new Thickness(8, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            repair.Click += async (_, _) =>
            {
                repair.IsEnabled = false;
                await onRepair(r.File);
                // The handler re-populates the panel, replacing this row.
            };
            Grid.SetColumn(repair, 4);
            grid.Children.Add(repair);
        }

        // Per-row Remove button: only for healthy (OK) rows, and only when the
        // panel provided a remove handler. Shares column 3 with Repair - the two
        // are mutually exclusive (a row is either a problem or OK).
        // NixOS: files are module-managed (declarative); per-file removal can't
        // work (read-only /etc, or only deletes a non-existent FHS path), so hide.
        if (onRemove != null && r.State == FileState.Ok && !Platform.Linux.NixOS.IsNixOS)
        {
            var remove = new Button
            {
                Content = Labels.Get("sysfiles_remove"),
                Classes = { "ghelper" },
                FontSize = 11,
                Padding = new Thickness(10, 3),
                Margin = new Thickness(8, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            remove.Click += async (_, _) =>
            {
                remove.IsEnabled = false;
                await onRemove(r.File);
                // The handler re-populates the panel, replacing this row.
            };
            Grid.SetColumn(remove, 4);
            grid.Children.Add(remove);
        }

        return grid;
    }

    private static (string glyph, string color) GlyphFor(FileState s) => s switch
    {
        FileState.Ok => ("\u2713", "#06B48A"),                 // ✓ green
        FileState.Unknown => ("\u2022", "#999999"),            // • gray
        FileState.Unavailable => ("\u2013", "#666666"),        // – dim
        FileState.NotApplicable => ("\u2013", "#666666"),      // – dim (n/a on this hardware)
        FileState.Disabled => ("\u26A0", "#FFA500"),            // ⚠ amber (service not enabled)
        _ => ("\u2717", "#FF2020"),                            // ✗ red (problem)
    };

    private static string StatusText(FileState s) => s switch
    {
        FileState.Ok => Labels.Get("sysfiles_status_ok"),
        FileState.Missing => Labels.Get("sysfiles_status_missing"),
        FileState.Outdated => Labels.Get("sysfiles_status_outdated"),
        FileState.WrongPerms => Labels.Get("sysfiles_status_wrongperms"),
        FileState.Unknown => Labels.Get("sysfiles_status_unknown"),
        FileState.Unavailable => Labels.Get("sysfiles_status_unavailable"),
        FileState.NotApplicable => Labels.Get("sysfiles_status_not_applicable"),
        FileState.Disabled => Labels.Get("sysfiles_status_disabled"),
        _ => Labels.Get("sysfiles_status_unknown"),
    };

    /// <summary>Stable, non-localized state name for logs/diagnostics. Explicit
    /// map (not StatusText, which is localized; not enum.ToString, to stay
    /// unambiguously AOT-safe).</summary>
    public static string StateLabel(FileState s) => s switch
    {
        FileState.Ok => "Ok",
        FileState.Missing => "Missing",
        FileState.Outdated => "Outdated",
        FileState.WrongPerms => "WrongPerms",
        FileState.Unknown => "Unknown",
        FileState.Unavailable => "Unavailable",
        FileState.NotApplicable => "NotApplicable",
        FileState.Disabled => "Disabled",
        _ => "Unknown",
    };

    /// <summary>One compact log line with every managed file's state, so a
    /// diagnostics dump (or live log) shows which integration files are
    /// Ok/Missing/Outdated/NotApplicable without needing a repair run.</summary>
    private static void LogStatus(List<FileResult> status)
        => Logger.WriteLine("Installer: integration files: "
            + string.Join(" ", status.Select(r => $"{r.File.Id}={StateLabel(r.State)}")));
}
