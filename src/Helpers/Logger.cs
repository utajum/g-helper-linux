namespace GHelper.Linux.Helpers;

/// <summary>
/// Simple file logger (ported from G-Helper's Logger.cs).
/// Logs to ~/.config/ghelper-linux/log.txt
/// </summary>
public static class Logger
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "ghelper-linux");

    private static readonly string LogFile = Path.Combine(LogDir, "log.txt");
    private static readonly object _lock = new();

    public static void WriteLine(string message)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            lock (_lock)
            {
                File.AppendAllText(LogFile, line + Environment.NewLine);
            }
            Console.WriteLine(line);
        }
        catch
        {
            // Logging should never crash the app
        }
    }

    public static void WriteLine(string message, Exception ex)
    {
        WriteLine($"{message}: {ex.Message}");
    }
}
