namespace GHelper.Linux.Helpers;

/// <summary>
/// Simple logger with in-memory ring buffer.
/// All output goes to stderr AND is retained in a 1000-line ring buffer
/// so diagnostics can include recent log history without log files.
/// </summary>
public static class Logger
{
    private const int BufferSize = 1000;
    private static readonly string[] _buffer = new string[BufferSize];
    private static int _writeIndex;
    private static int _count;
    private static long _totalLines;
    private static readonly object _lock = new();

    public static void WriteLine(string message)
    {
        try
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            Console.Error.WriteLine(line);

            lock (_lock)
            {
                _buffer[_writeIndex] = line;
                _writeIndex = (_writeIndex + 1) % BufferSize;
                if (_count < BufferSize) _count++;
                _totalLines++;
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

    /// <summary>
    /// Returns the most recent log lines in chronological order.
    /// Thread-safe snapshot — callers get a copy, not a live view.
    /// </summary>
    public static string[] GetRecentLines()
    {
        lock (_lock)
        {
            var result = new string[_count];
            if (_count < BufferSize)
            {
                // Buffer hasn't wrapped yet — lines are 0.._count-1
                Array.Copy(_buffer, 0, result, 0, _count);
            }
            else
            {
                // Buffer has wrapped — oldest line is at _writeIndex
                int firstChunk = BufferSize - _writeIndex;
                Array.Copy(_buffer, _writeIndex, result, 0, firstChunk);
                Array.Copy(_buffer, 0, result, firstChunk, _writeIndex);
            }
            return result;
        }
    }

    /// <summary>Total number of lines written since app start (may exceed buffer size).</summary>
    public static long TotalLines
    {
        get { lock (_lock) { return _totalLines; } }
    }
}
