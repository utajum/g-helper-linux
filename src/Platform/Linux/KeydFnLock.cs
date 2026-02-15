namespace GHelper.Linux.Platform.Linux;

/// <summary>
/// keyd-based FN Lock implementation for laptops without hardware FN Lock support.
/// 
/// keyd is a system-wide keyboard remapping daemon that works on X11, Wayland, and console.
/// It supports layers, making it perfect for FN Lock toggle functionality.
/// 
/// Installation: sudo apt install keyd
/// 
/// How it works:
/// 1. Detects if keyd is installed and running
/// 2. Generates a keyd configuration file with an 'fnlock' layer
/// 3. The fnlock layer remaps F1-F12 to media keys
/// 4. When FN Lock is ON: activates the fnlock layer
/// 5. When FN Lock is OFF: deactivates the fnlock layer (normal F1-F12)
/// </summary>
public class KeydFnLock : IDisposable
{
    private const string KeydConfigDir = "/etc/keyd";
    private const string KeydConfigFile = "ghelper-fnlock.conf";
    private const string KeydSocketPath = "/var/run/keyd.socket";
    
    private bool _isInstalled;
    private bool _isRunning;
    private bool _isEnabled;
    
    /// <summary>
    /// Check if keyd is installed on the system.
    /// </summary>
    public static bool IsInstalled()
    {
        // Check for keyd binary in common locations
        string[] paths = {
            "/usr/bin/keyd",
            "/usr/local/bin/keyd",
            "/bin/keyd"
        };
        
        foreach (var path in paths)
        {
            if (File.Exists(path))
                return true;
        }
        
        // Also check using 'which'
        try
        {
            var result = RunCommand("which", "keyd");
            if (!string.IsNullOrEmpty(result) && !result.Contains("not found"))
                return true;
        }
        catch { }
        
        return false;
    }
    
    /// <summary>
    /// Check if keyd daemon is running.
    /// </summary>
    public static bool IsRunning()
    {
        // Check if socket exists (keyd creates this when running)
        if (File.Exists(KeydSocketPath))
            return true;
        
        // Also check using systemctl
        try
        {
            var result = RunCommand("systemctl", "is-active keyd");
            if (result?.Trim() == "active")
                return true;
        }
        catch { }
        
        return false;
    }
    
    /// <summary>
    /// Initialize keyd FN Lock support.
    /// Returns true if successfully initialized.
    /// </summary>
    public bool Init()
    {
        _isInstalled = IsInstalled();
        if (!_isInstalled)
        {
            Helpers.Logger.WriteLine("KeydFnLock: keyd is not installed");
            return false;
        }
        
        _isRunning = IsRunning();
        if (!_isRunning)
        {
            Helpers.Logger.WriteLine("KeydFnLock: keyd is installed but not running");
            // Try to start it
            try
            {
                RunCommand("sudo", "systemctl start keyd");
                Thread.Sleep(500); // Give it time to start
                _isRunning = IsRunning();
                if (!_isRunning)
                {
                    Helpers.Logger.WriteLine("KeydFnLock: Failed to start keyd daemon");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Helpers.Logger.WriteLine("KeydFnLock: Failed to start keyd", ex);
                return false;
            }
        }
        
        // Generate and install config
        if (!InstallConfig())
        {
            Helpers.Logger.WriteLine("KeydFnLock: Failed to install config");
            return false;
        }
        
        Helpers.Logger.WriteLine("KeydFnLock: Initialized successfully");
        return true;
    }
    
    /// <summary>
    /// Generate and install the keyd configuration file.
    /// </summary>
    private bool InstallConfig()
    {
        try
        {
            // Ensure config directory exists
            if (!Directory.Exists(KeydConfigDir))
            {
                Helpers.Logger.WriteLine($"KeydFnLock: Creating {KeydConfigDir}");
                RunCommand("sudo", $"mkdir -p {KeydConfigDir}");
            }
            
            // Generate config content
            var config = GenerateConfig();
            var configPath = Path.Combine(KeydConfigDir, KeydConfigFile);
            
            // Write to temp file first, then move with sudo
            var tempFile = Path.GetTempFileName();
            File.WriteAllText(tempFile, config);
            
            try
            {
                RunCommand("sudo", $"mv {tempFile} {configPath}");
                RunCommand("sudo", $"chmod 644 {configPath}");
            }
            catch
            {
                File.Delete(tempFile);
                throw;
            }
            
            // Reload keyd to pick up new config
            ReloadKeyd();
            
            Helpers.Logger.WriteLine($"KeydFnLock: Config installed to {configPath}");
            return true;
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine("KeydFnLock: Failed to install config", ex);
            return false;
        }
    }
    
    /// <summary>
    /// Generate the keyd configuration for FN Lock.
    /// </summary>
    private string GenerateConfig()
    {
        // keyd configuration syntax:
        // [ids] - list of device IDs to apply this config to
        // [main] - base layer
        // [fnlock] - FN Lock layer (toggles F1-F12 to media keys)
        
        return @"# G-Helper FN Lock Configuration
# Auto-generated by G-Helper Linux
# Do not edit manually - changes will be overwritten

[ids]
*               # Apply to all keyboards

[main]
# Base configuration - normal key behavior
# When FN Lock is OFF, F1-F12 work as function keys

[fnlock]
# FN Lock layer - F1-F12 act as media keys
# Toggled by G-Helper when FN Lock is enabled

# F1-F12 to media keys mapping
f1 = mute
f2 = volumedown
f3 = volumeup
f4 = micmute
f5 = brightnessdown
f6 = brightnessup
f7 = kbdillumdown
f8 = kbdillumup
f9 = prog1
f10 = touchpad_toggle
f11 = sleep
f12 = wlan

# Keep Fn+F1-F12 as actual F1-F12 when FN Lock is on
# This requires knowing the Fn key - common values:
# Left side (standard Fn key)
leftmeta+f1 = f1
leftmeta+f2 = f2
leftmeta+f3 = f3
leftmeta+f4 = f4
leftmeta+f5 = f5
leftmeta+f6 = f6
leftmeta+f7 = f7
leftmeta+f8 = f8
leftmeta+f9 = f9
leftmeta+f10 = f10
leftmeta+f11 = f11
leftmeta+f12 = f12
";
    }
    
    /// <summary>
    /// Toggle the FN Lock layer on/off.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        if (!_isInstalled || !_isRunning)
        {
            Helpers.Logger.WriteLine($"KeydFnLock: Cannot set enabled={enabled}, keyd not ready");
            return;
        }
        
        _isEnabled = enabled;
        
        try
        {
            if (enabled)
            {
                // Activate the fnlock layer
                // keyd doesn't have a direct "activate layer" command, 
                // but we can use a toggle mechanism
                RunCommand("sudo", "keyd -e 'layer fnlock'");
                Helpers.Logger.WriteLine("KeydFnLock: FN Lock layer activated");
            }
            else
            {
                // Deactivate the fnlock layer (return to main)
                RunCommand("sudo", "keyd -e 'layer main'");
                Helpers.Logger.WriteLine("KeydFnLock: FN Lock layer deactivated");
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.WriteLine($"KeydFnLock: Failed to set enabled={enabled}", ex);
        }
    }
    
    /// <summary>
    /// Reload keyd to pick up configuration changes.
    /// </summary>
    private void ReloadKeyd()
    {
        try
        {
            // Try reloading via systemctl first
            RunCommand("sudo", "systemctl reload keyd");
        }
        catch
        {
            // Fallback: send HUP signal
            try
            {
                RunCommand("sudo", "killall -HUP keyd");
            }
            catch { }
        }
    }
    
    /// <summary>
    /// Get the installation command to show to the user.
    /// </summary>
    public static string GetInstallCommand()
    {
        return "sudo apt install keyd && sudo systemctl enable keyd --now";
    }
    
    /// <summary>
    /// Run a shell command and return output.
    /// </summary>
    private static string? RunCommand(string command, string arguments)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null) return null;
            
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            
            return output;
        }
        catch
        {
            return null;
        }
    }
    
    public void Dispose()
    {
        // Deactivate fnlock layer on dispose
        if (_isEnabled)
        {
            try
            {
                SetEnabled(false);
            }
            catch { }
        }
    }
}
