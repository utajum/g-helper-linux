namespace GHelper.Linux.Helpers;

/// <summary>
/// Simple logger with file + stdout output.
/// Log file: ~/.config/ghelper-linux/log.txt
/// </summary>
public static class Logger
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "ghelper-linux");

    public static readonly string LogFile = Path.Combine(LogDir, "log.txt");

    private static StreamWriter? _writer;
    private static readonly object _lock = new();

    static Logger()
    {
        try
        {
            Directory.CreateDirectory(LogDir);

            // Rotate if > 1MB
            if (File.Exists(LogFile))
            {
                var info = new FileInfo(LogFile);
                if (info.Length > 1024 * 1024)
                {
                    var backup = LogFile + ".old";
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Move(LogFile, backup);
                }
            }

            _writer = new StreamWriter(LogFile, append: true)
            {
                AutoFlush = true
            };
        }
        catch
        {
            // If we can't write log file, continue with stdout only
        }
    }

    public static void WriteLine(string message)
    {
        try
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            Console.WriteLine(line);

            lock (_lock)
            {
                _writer?.WriteLine(line);
            }
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
