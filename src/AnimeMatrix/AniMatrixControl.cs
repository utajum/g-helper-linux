using GHelper.Linux.Helpers;
using SkiaSharp;

namespace GHelper.Linux.AnimeMatrix;

/// <summary>
/// High-level AnimeMatrix / Slash LED controller.
/// Detects hardware on construction and dispatches display commands through the appropriate driver.
/// </summary>
public class AniMatrixControl : IDisposable
{
    /// <summary>AnimeMatrix dot-matrix display device, if present.</summary>
    public AnimeMatrixDevice? deviceMatrix;

    /// <summary>Slash LED bar device, if present.</summary>
    public SlashDevice? deviceSlash;

    public bool IsValid => deviceMatrix is not null || deviceSlash is not null;
    public bool IsSlash => deviceSlash is not null;

    /// <summary>Set by lid-close detection so display routines can skip updates.</summary>
    public static bool lidClose;

    private static bool _wakeUp;

    private readonly System.Timers.Timer _matrixTimer;
    private System.Timers.Timer? _slashTimer;

    /// <summary>Probes for AnimeMatrix or Slash hardware. Detection failures are logged.</summary>
    public AniMatrixControl()
    {
        try
        {
            if (AppConfig.IsSlash())
            {
                deviceSlash = SlashDevice.Detect();
                if (deviceSlash is not null)
                    Logger.WriteLine("AniMatrixControl: Slash device detected");
            }
            else if (IsAnimeMatrix())
            {
                deviceMatrix = new AnimeMatrixDevice();
                Logger.WriteLine("AniMatrixControl: AnimeMatrix device detected");
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"AniMatrixControl: device init failed: {ex.Message}");
        }

        _matrixTimer = new System.Timers.Timer(100) { AutoReset = true };
        _matrixTimer.Elapsed += MatrixTimer_Elapsed;
    }

    /// <summary>Reads config and applies the current display mode to whichever device is present.</summary>
    public void SetDevice(bool wakeUp = false)
    {
        if (deviceMatrix is not null)
            SetMatrix(wakeUp);
        if (deviceSlash is not null)
            SetSlash(wakeUp);
    }

    /// <summary>Applies the current Slash LED bar mode from config.</summary>
    public void SetSlash(bool wakeUp = false)
    {
        if (deviceSlash is null)
            return;

        int brightness = AppConfig.Get("matrix_brightness", 0);
        int running = AppConfig.Get("matrix_running", 0);
        int interval = AppConfig.Get("matrix_interval", 0);

        bool auto = AppConfig.Is("matrix_auto");
        bool lid = AppConfig.Is("matrix_lid");

        // Mode Off here: the Slash follows the Extra window settings (slash_* keys).
        if (running == 0)
        {
            Task.Run(USB.Slash.Init);
            return;
        }

        Task.Run(() =>
        {
            try
            {
                deviceSlash.SetProvider();
            }
            catch (Exception ex)
            {
                Logger.WriteLine(ex.Message);
                return;
            }

            if (wakeUp)
                _wakeUp = true;

            bool onBattery = !(App.Power?.IsOnAcPower() ?? true);

            if (running == 0 || brightness == 0 || (auto && onBattery) || (lid && lidClose))
            {
                deviceSlash.SetSleepActive(false);
                deviceSlash.SetEnabled(false);
                Logger.WriteLine("Slash Off");
            }
            else
            {
                if (_wakeUp)
                {
                    deviceSlash.WakeUp();
                    _wakeUp = false;
                }

                deviceSlash.SetEnabled(true);
                deviceSlash.Init();

                // UI combo index 0 is Off, so firmware modes start at 1
                SlashMode mode = (SlashMode)(running - 1);

                switch (mode)
                {
                    case SlashMode.Static:
                        var custom = AppConfig.GetString("slash_custom");
                        if (!string.IsNullOrEmpty(custom))
                        {
                            Logger.WriteLine("Slash: Static");
                            deviceSlash.SetCustom(AppConfig.StringToBytes(custom));
                        }
                        else
                        {
                            deviceSlash.SetMode(mode);
                            deviceSlash.SetOptions(true, brightness, interval);
                            deviceSlash.Save();
                        }
                        break;
                    case SlashMode.BatteryLevel:
                        Logger.WriteLine("Slash: Battery Level");
                        SlashTimer_Start();
                        SlashTimer_Tick();
                        break;
                    case SlashMode.Audio:
                    case SlashMode.AudioSpectrum:
                        Logger.WriteLine("Slash: audio visualizer not supported yet");
                        deviceSlash.SetMode(SlashMode.Bounce);
                        deviceSlash.SetOptions(true, brightness, interval);
                        deviceSlash.Save();
                        break;
                    default:
                        deviceSlash.SetMode(mode);
                        deviceSlash.SetOptions(true, brightness, interval);
                        deviceSlash.Save();
                        break;
                }

                deviceSlash.SetSleepActive(AppConfig.IsNotFalse("slash_sleep"));
            }
        });
    }

    /// <summary>Applies the current AnimeMatrix display mode from config.</summary>
    public void SetMatrix(bool wakeUp = false)
    {
        if (deviceMatrix is null)
            return;

        int brightness = AppConfig.Get("matrix_brightness", 0);
        int running = AppConfig.Get("matrix_running", 0);
        bool auto = AppConfig.Is("matrix_auto");
        bool lid = AppConfig.Is("matrix_lid");

        StopMatrixTimer();

        Task.Run(() =>
        {
            try
            {
                deviceMatrix.SetProvider();
            }
            catch (Exception ex)
            {
                Logger.WriteLine(ex.Message);
                return;
            }

            if (wakeUp)
                deviceMatrix.WakeUp();

            bool onBattery = !(App.Power?.IsOnAcPower() ?? true);

            if (running == 0 || brightness == 0 || (auto && onBattery) || (lid && lidClose))
            {
                deviceMatrix.SetDisplayState(false);
                deviceMatrix.SetDisplayState(false); // some devices need it twice
                Logger.WriteLine("Matrix Off");
            }
            else
            {
                deviceMatrix.SetDisplayState(true);
                deviceMatrix.SetBrightness((BrightnessMode)brightness);

                // UI combo: 0 Off, 1-2 built-in, 3 picture, 4 clock, 5 audio
                switch (running)
                {
                    case 3:
                        SetMatrixPicture(AppConfig.GetString("matrix_picture"));
                        break;
                    case 4:
                        SetMatrixClock();
                        break;
                    case 5:
                        Logger.WriteLine("Matrix: audio visualizer not supported yet");
                        SetBuiltIn(0);
                        break;
                    default:
                        SetBuiltIn(running - 1);
                        break;
                }
            }
        });
    }

    /// <summary>Enables a built-in firmware animation set for the given running variant.</summary>
    private void SetBuiltIn(int running)
    {
        if (deviceMatrix is null)
            return;

        BuiltInAnimation animation = new(
            (BuiltInAnimation.Running)Math.Clamp(running, 0, 1),
            (BuiltInAnimation.Sleeping)AppConfig.Get("matrix_sleep", (int)BuiltInAnimation.Sleeping.Starfield),
            (BuiltInAnimation.Shutdown)AppConfig.Get("matrix_shutdown", (int)BuiltInAnimation.Shutdown.SeeYa),
            (BuiltInAnimation.Startup)AppConfig.Get("matrix_startup", (int)BuiltInAnimation.Startup.StaticEmergence)
        );
        deviceMatrix.SetBuiltInAnimation(true, animation);
        Logger.WriteLine("Matrix builtin: " + animation.AsByte);
    }

    /// <summary>Renders a static picture or animated GIF on the matrix display.</summary>
    public void SetMatrixPicture(string? fileName)
    {
        if (deviceMatrix is null)
            return;

        if (string.IsNullOrEmpty(fileName) || !File.Exists(fileName))
        {
            Logger.WriteLine($"Matrix: picture not found: {fileName}");
            return;
        }

        StopMatrixTimer();

        try
        {
            using var stream = File.OpenRead(fileName);
            using var codec = SKCodec.Create(stream);
            ProcessPicture(codec);
            Logger.WriteLine("Matrix " + fileName);
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Matrix: error loading picture: {ex.Message}");
        }
    }

    /// <summary>Starts the 1-second clock display on the matrix.</summary>
    public void SetMatrixClock()
    {
        if (deviceMatrix is null)
            return;

        deviceMatrix.SetBuiltInAnimation(false);
        StartMatrixTimer(1000);
        Logger.WriteLine("Matrix Clock");
    }

    /// <summary>Handles lid open/close events. Turns display off if matrix_lid is enabled.</summary>
    public void SetLidMode(bool force = false)
    {
        bool matrixLid = AppConfig.Is("matrix_lid");

        deviceSlash?.SetLidCloseAnimation(!matrixLid && !AppConfig.Is("slash_sleep"));

        if (matrixLid || force)
        {
            Logger.WriteLine($"Matrix LidClosed: {lidClose}");
            SetDevice(true);
        }
    }

    /// <summary>Re-applies display state when AC/battery power changes, honouring matrix_auto.</summary>
    public void SetBatteryAuto()
    {
        if (deviceSlash is not null && AppConfig.Get("matrix_running", 0) != 0)
        {
            bool auto = AppConfig.Is("matrix_auto");
            deviceSlash.SetBatterySaver(auto);
            if (!auto)
                SetSlash();
        }

        if (deviceMatrix is not null)
            SetMatrix();
    }

    public static bool IsAnimeMatrix()
    {
        return AppConfig.IsAnimeMatrix();
    }

    // Timers

    private void StartMatrixTimer(int interval = 100)
    {
        _matrixTimer.Interval = interval;
        _matrixTimer.Start();
    }

    private void StopMatrixTimer()
    {
        _matrixTimer.Stop();
    }

    private void MatrixTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (deviceMatrix is null)
            return;

        try
        {
            switch (AppConfig.Get("matrix_running"))
            {
                case 3:
                    deviceMatrix.PresentNextFrame();
                    break;
                case 4:
                    PresentClock();
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Matrix timer: {ex.Message}");
        }
    }

    private void SlashTimer_Start(int interval = 180000)
    {
        if (_slashTimer is null)
        {
            _slashTimer = new System.Timers.Timer(interval) { AutoReset = true };
            _slashTimer.Elapsed += (_, _) => SlashTimer_Tick();
        }

        if (Math.Abs(_slashTimer.Interval - interval) > 0.1)
            _slashTimer.Interval = interval;

        _slashTimer.Start();
    }

    private void SlashTimer_Tick()
    {
        if (deviceSlash is null)
            return;

        // Kill the timer if the battery pattern is no longer active
        if ((SlashMode)(AppConfig.Get("matrix_running", 0) - 1) != SlashMode.BatteryLevel)
        {
            _slashTimer?.Stop();
            return;
        }

        deviceSlash.SetBatteryPattern(AppConfig.Get("matrix_brightness", 0));
    }

    // Clock rendering

    private void PresentClock()
    {
        if (deviceMatrix is null)
            return;

        string timeFormat = AppConfig.GetString("matrix_time", "HH:mm") ?? "HH:mm";
        string dateFormat = AppConfig.GetString("matrix_date", "yy.MM.dd") ?? "yy.MM.dd";

        if (DateTime.Now.Second % 2 != 0)
            timeFormat = timeFormat.Replace(":", "  ");

        deviceMatrix.Clear();

        switch (AnimeMatrixDevice.DetectModel())
        {
            case AnimeType.STRIX:
                DrawTextDiagonal(DateTime.Now.ToString(timeFormat), 15, 4, 20);
                break;
            default:
                DrawTextDiagonal(DateTime.Now.ToString(timeFormat), 15, 7 - deviceMatrix.FullRows / 2, 25);
                DrawTextDiagonal(DateTime.Now.ToString(dateFormat), 11.5f, 0, 14);
                break;
        }

        deviceMatrix.Present();
    }

    /// <summary>Rasterises text with SkiaSharp and writes it to the display buffer diagonally.</summary>
    private void DrawTextDiagonal(string text, float fontSize, int x, int y)
    {
        if (deviceMatrix is null)
            return;

        int width = deviceMatrix.MaxRows;
        int height = deviceMatrix.MaxRows - deviceMatrix.FullRows;

        using var bmp = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Black);

        using var typeface = SKTypeface.FromFamilyName("monospace", SKFontStyle.Normal);
        using var font = new SKFont(typeface, fontSize);
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = false };

        float baseline = height - y - font.Metrics.Ascent;
        canvas.DrawText(text, x, baseline, SKTextAlign.Left, font, paint);

        var pixels = bmp.Pixels;
        for (int py = 0; py < height; py++)
        {
            for (int px = 0; px < width; px++)
            {
                var pixel = pixels[py * width + px];
                int color = (pixel.Red + pixel.Green + pixel.Blue) / 3;
                if (color > 20)
                    deviceMatrix.SetLedDiagonal(px, py, (byte)color, 5,
                        height - deviceMatrix.FullRows / 2 - 1);
            }
        }
    }

    // Picture rendering

    private void ProcessPicture(SKCodec codec)
    {
        var dev = deviceMatrix!;

        dev.SetBuiltInAnimation(false);
        dev.ClearFrames();

        int panX = AppConfig.Get("matrix_x", 0);
        int panY = AppConfig.Get("matrix_y", 0);
        int zoom = AppConfig.Get("matrix_zoom", 100);
        int contrast = AppConfig.Get("matrix_contrast", 100);
        int gamma = AppConfig.Get("matrix_gamma", 0);
        int speed = AppConfig.Get("matrix_speed", 50);
        int quality = AppConfig.Get("matrix_quality", 0);
        var rotation = (MatrixRotation)AppConfig.Get("matrix_rotation", 0);

        int frameCount = codec.FrameCount;

        if (frameCount > 1)
        {
            int frameDelay = codec.FrameInfo.Length > 0 ? codec.FrameInfo[0].Duration : 0;
            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height,
                SKColorType.Rgba8888, SKAlphaType.Premul);
            using var frame = new SKBitmap(info);

            for (int i = 0; i < frameCount; i++)
            {
                var options = i > 0 ? new SKCodecOptions(i, i - 1) : new SKCodecOptions(i);
                codec.GetPixels(info, frame.GetPixels(), options);

                GenerateFrame(frame, rotation, zoom, panX, panY, quality, contrast, gamma);
                dev.AddFrame();
            }

            Logger.WriteLine($"Matrix GIF: {frameCount} frames, delay {frameDelay}ms");
            StartMatrixTimer(Math.Max(speed, frameDelay));
        }
        else
        {
            using var image = SKBitmap.Decode(codec);
            if (image is null)
            {
                Logger.WriteLine("Matrix: failed to decode image");
                return;
            }

            GenerateFrame(image, rotation, zoom, panX, panY, quality, contrast, gamma);
            dev.Present();
        }
    }

    /// <summary>Scales the image into the LED grid space and loads it into the display buffer.</summary>
    private void GenerateFrame(SKBitmap image, MatrixRotation rotation,
        int zoom, int panX, int panY, int quality, int contrast, int gamma)
    {
        var dev = deviceMatrix!;

        if (rotation == MatrixRotation.Planar)
        {
            // Logical canvas is wider than the LED grid; pixels are squashed horizontally
            int width = dev.MaxColumns / 2 * 6;
            int height = dev.MaxRows;
            int targetWidth = dev.MaxColumns * 2;

            float scale = Math.Min((float)width / image.Width, (float)height / image.Height) * zoom / 100f;
            float scaleWidth = image.Width * scale;
            float scaleHeight = image.Height * scale;

            float drawX = (float)Math.Round(targetWidth - (scaleWidth + panX) * targetWidth / width);
            float drawW = (float)Math.Round(scaleWidth * targetWidth / width);

            byte[] gray = RasterizeGray(image, targetWidth, height, drawX, -panY, drawW, scaleHeight, quality);
            dev.GenerateFrame(gray, targetWidth, height, contrast, gamma);
        }
        else
        {
            int width = dev.MaxRows + dev.FullRows;
            int height = dev.MaxColumns + dev.FullRows;
            if (image.Height / image.Width > height / width)
                height = dev.MaxColumns;

            float scale = Math.Min((float)width / image.Width, (float)height / image.Height) * zoom / 100f;
            float scaleWidth = image.Width * scale;
            float scaleHeight = image.Height * scale;

            byte[] gray = RasterizeGray(image, width, height,
                (width - scaleWidth) / 2, height - scaleHeight, scaleWidth, scaleHeight, quality);
            dev.GenerateFrameDiagonal(gray, width, height, -panX, panY, contrast, gamma);
        }
    }

    /// <summary>Draws the image into a small canvas and returns its grayscale pixel buffer.</summary>
    private static byte[] RasterizeGray(SKBitmap image, int canvasWidth, int canvasHeight,
        float drawX, float drawY, float drawWidth, float drawHeight, int quality)
    {
        using var bmp = new SKBitmap(canvasWidth, canvasHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Black);

        var sampling = quality switch
        {
            2 => new SKSamplingOptions(SKCubicResampler.Mitchell),
            4 => new SKSamplingOptions(SKCubicResampler.CatmullRom),
            5 => new SKSamplingOptions(SKFilterMode.Nearest),
            _ => new SKSamplingOptions(SKFilterMode.Linear),
        };

        using var paint = new SKPaint { IsAntialias = quality != 5 };
        using var srcImage = SKImage.FromBitmap(image);
        var srcRect = new SKRect(0, 0, image.Width, image.Height);
        var destRect = new SKRect(drawX, drawY, drawX + drawWidth, drawY + drawHeight);
        canvas.DrawImage(srcImage, srcRect, destRect, sampling, paint);

        var pixels = bmp.Pixels;
        byte[] gray = new byte[canvasWidth * canvasHeight];
        for (int i = 0; i < gray.Length; i++)
        {
            var pixel = pixels[i];
            gray[i] = (byte)((pixel.Red + pixel.Green + pixel.Blue) / 3);
        }

        return gray;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _matrixTimer.Stop();
        _matrixTimer.Dispose();

        _slashTimer?.Stop();
        _slashTimer?.Dispose();
        _slashTimer = null;

        deviceMatrix?.Dispose();
        deviceMatrix = null;

        deviceSlash?.Dispose();
        deviceSlash = null;
    }

    // Config keys:
    //  matrix_brightness (0-3), matrix_running (0-5: off/builtin1/builtin2/picture/clock/audio),
    //  matrix_picture (path), matrix_zoom (10-200), matrix_contrast (10-200),
    //  matrix_gamma (-100..100), matrix_speed, matrix_rotation (0=Planar, 1=Diagonal),
    //  matrix_quality, matrix_x, matrix_y, matrix_time, matrix_date,
    //  matrix_auto (bool), matrix_lid (bool), matrix_sleep, matrix_shutdown,
    //  matrix_startup, matrix_interval, slash_custom (hex), slash_sleep (bool)
}
