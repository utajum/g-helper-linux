namespace GHelper.Linux.Helpers;

/// <summary>
/// Simple stdout-only logger.
/// All output goes to the console — no log files.
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
