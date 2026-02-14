using Avalonia;
using GHelper.Linux;

// G-Helper for Linux — single binary ASUS laptop control
// Port of https://github.com/seerge/g-helper

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
