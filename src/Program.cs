using Avalonia;
using GHelper.Linux;

// G-Helper for Linux — single binary ASUS laptop control
// Port of https://github.com/seerge/g-helper

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // --verbose or -v enables console logging (default: file-only)
        if (args.Any(a => a is "--verbose" or "-v"))
            GHelper.Linux.Helpers.Logger.Verbose = true;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
