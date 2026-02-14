using Avalonia;
using GHelper.Linux;
using GHelper.Linux.Helpers;

// G-Helper for Linux — single binary ASUS laptop control
// Port of https://github.com/seerge/g-helper

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Extract and preload embedded native libraries (libSkiaSharp.so, libHarfBuzzSharp.so)
        // before any Avalonia/SkiaSharp code runs.
        NativeLibExtractor.ExtractAndLoad();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
