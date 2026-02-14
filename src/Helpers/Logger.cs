namespace GHelper.Linux.Helpers;

/// <summary>
/// Simple console logger (ported from G-Helper's Logger.cs).
/// Logs to stdout only — no log files.
/// </summary>
public static class Logger
{
    public static void WriteLine(string message)
    {
        try
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
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
