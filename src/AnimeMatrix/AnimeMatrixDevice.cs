using System.Text;
using GHelper.Linux.AnimeMatrix.Communication;
using GHelper.Linux.Helpers;

namespace GHelper.Linux.AnimeMatrix;

/// <summary>
/// Anime matrix display type, determined by laptop model.
/// </summary>
public enum AnimeType
{
    /// <summary>ROG Zephyrus G14 2021 (GA401). Diagonal LED layout.</summary>
    GA401,

    /// <summary>ROG Zephyrus G14 2022+ (GA402). Planar LED layout.</summary>
    GA402,

    /// <summary>ROG Zephyrus M16 (GU604). Large planar LED layout.</summary>
    GU604,

    /// <summary>ROG Strix SCAR (G615/G635/G815/G835). Diagonal LED layout.</summary>
    STRIX
}

/// <summary>
/// Display brightness levels for the AnimeMatrix panel.
/// </summary>
public enum BrightnessMode : byte
{
    Off = 0,
    Dim = 1,
    Medium = 2,
    Full = 3
}

/// <summary>
/// Physical LED rotation of the AnimeMatrix panel.
/// </summary>
public enum MatrixRotation
{
    /// <summary>LEDs arranged in a regular planar grid.</summary>
    Planar = 0,

    /// <summary>LEDs arranged diagonally (half-pixel offset per row).</summary>
    Diagonal = 1
}

/// <summary>
/// HID packet for AnimeMatrix commands. Report ID 0x5E, 640 bytes.
/// </summary>
internal class AnimeMatrixPacket : Packet
{
    public AnimeMatrixPacket(byte[] command)
        : base(0x5E, 640, command)
    {
    }
}

/// <summary>
/// Describes a combination of built-in firmware animations for each device state.
/// Each state has two animation variants encoded as a single bit.
/// </summary>
public class BuiltInAnimation
{
    /// <summary>Animation variants while the device is actively running.</summary>
    public enum Running
    {
        BinaryBannerScroll = 0,
        RogLogoGlitch = 1
    }

    /// <summary>Animation variants while the device is sleeping.</summary>
    public enum Sleeping
    {
        BannerSwipe = 0,
        Starfield = 1
    }

    /// <summary>Animation variants during shutdown.</summary>
    public enum Shutdown
    {
        GlitchOut = 0,
        SeeYa = 1
    }

    /// <summary>Animation variants during startup.</summary>
    public enum Startup
    {
        GlitchConstruction = 0,
        StaticEmergence = 1
    }

    /// <summary>
    /// Packed byte representing all four animation selections.
    /// Bit layout: [startup:3][shutdown:2][sleeping:1][running:0].
    /// </summary>
    public byte AsByte { get; }

    public BuiltInAnimation(
        Running running,
        Sleeping sleeping,
        Shutdown shutdown,
        Startup startup)
    {
        AsByte |= (byte)(((int)running & 0x01) << 0);
        AsByte |= (byte)(((int)sleeping & 0x01) << 1);
        AsByte |= (byte)(((int)shutdown & 0x01) << 2);
        AsByte |= (byte)(((int)startup & 0x01) << 3);
    }
}

/// <summary>
/// Controls the ASUS ROG AnimeMatrix LED panel on the laptop lid.
/// Supported: GA401 (33x55), GA402 (34x61, default), GU604 (39x92), STRIX (34x68).
/// Display buffer is a flat byte array; Present() splits it into HID pages.
/// </summary>
public class AnimeMatrixDevice : Device
{
    private int UpdatePageLength = 490;
    private int LedCount = 1450;

    private byte[] _displayBuffer;
    private readonly List<byte[]> frames = new();

    /// <summary>Maximum rows in the LED grid.</summary>
    public int MaxRows = 61;

    /// <summary>Maximum columns in the widest row.</summary>
    public int MaxColumns = 34;

    /// <summary>Linear address offset for the first LED (1 for GA401, 0 for others).</summary>
    public int LedStart;

    /// <summary>Number of leading rows with special pitch (header rows).</summary>
    public int FullRows = 11;

    private int frameIndex;

    private static AnimeType _model = AnimeType.GA402;

    /// <inheritdoc />
    protected override string LogName => "Matrix";

    /// <summary>Initialises the device, detecting the model and LED geometry.</summary>
    public AnimeMatrixDevice() : base(0x0B05, 0x193B, 640)
    {
        if (AppConfig.ContainsModel("401"))
        {
            _model = AnimeType.GA401;
            MaxColumns = 33;
            MaxRows = 55;
            LedCount = 1245;
            UpdatePageLength = 410;
            FullRows = 5;
            LedStart = 1;
        }

        if (AppConfig.ContainsModel("GU604"))
        {
            _model = AnimeType.GU604;
            MaxColumns = 39;
            MaxRows = 92;
            LedCount = 1711;
            UpdatePageLength = 630;
            FullRows = 9;
        }

        if (AppConfig.ContainsModel("G635") || AppConfig.ContainsModel("G615") ||
            AppConfig.ContainsModel("G835") || AppConfig.ContainsModel("G815"))
        {
            _model = AnimeType.STRIX;
            MaxColumns = 34;
            MaxRows = 68;
            LedCount = 810;
            UpdatePageLength = 490;
            FullRows = 29;
        }

        _displayBuffer = new byte[LedCount];
    }

    /// <summary>Creates an AnimeMatrixPacket (AOT-safe).</summary>
    private static AnimeMatrixPacket CreatePacket(params byte[] command) => new(command);

    /// <summary>Sends the "ASUS Tech.Inc." wake-up handshake.</summary>
    public void WakeUp()
    {
        Set(CreatePacket(Encoding.ASCII.GetBytes("ASUS Tech.Inc.")));
    }

    /// <summary>Sets the display brightness level.</summary>
    public void SetBrightness(BrightnessMode mode)
    {
        Set(CreatePacket(0xC0, 0x04, (byte)mode));
    }

    /// <summary>Enables or disables the AnimeMatrix display output.</summary>
    public void SetDisplayState(bool enable)
    {
        Set(CreatePacket(0xC3, 0x01, enable ? (byte)0x00 : (byte)0x80));
    }

    /// <summary>Enables or disables firmware built-in animations.</summary>
    public void SetBuiltInAnimation(bool enable)
    {
        Set(CreatePacket(0xC4, 0x01, enable ? (byte)0x00 : (byte)0x80));
    }

    /// <summary>Enables or disables built-in animations with a specific animation selection.</summary>
    public void SetBuiltInAnimation(bool enable, BuiltInAnimation animation)
    {
        SetBuiltInAnimation(enable);
        Set(CreatePacket(0xC5, animation.AsByte));
    }

    /// <summary>Returns the column count (logical width) for the given row.</summary>
    public int Width(int y)
    {
        return _model switch
        {
            AnimeType.GA401 => 33,
            AnimeType.GU604 => 39,
            AnimeType.STRIX => 1 + y / 2,
            _ => 34, // GA402
        };
    }

    /// <summary>Returns the first valid X for a row. Rows past FullRows have an increasing left margin.</summary>
    public int FirstX(int y)
    {
        return _model switch
        {
            AnimeType.GA401 => (y < 5 && y % 2 == 0)
                ? 1
                : (int)Math.Ceiling(Math.Max(0, y - FullRows) / 2F),

            AnimeType.GU604 => (y < 9 && y % 2 == 0)
                ? 1
                : (int)Math.Ceiling(Math.Max(0, y - FullRows) / 2F),

            _ => (int)Math.Ceiling(Math.Max(0, y - FullRows) / 2F),
        };
    }

    /// <summary>Returns the physical LED count (pitch) for the given row.</summary>
    public int Pitch(int y)
    {
        switch (_model)
        {
            case AnimeType.GA401:
                return y switch
                {
                    0 or 2 or 4 => 33,
                    1 or 3 => 35,
                    _ => 36 - y / 2,
                };

            case AnimeType.GU604:
                return y switch
                {
                    0 or 2 or 4 or 6 or 8 => 38,
                    1 or 3 or 5 or 7 or 9 => 39,
                    _ => Width(y) - FirstX(y),
                };

            default:
                return Width(y) - FirstX(y);
        }
    }

    /// <summary>Converts a row index to the linear byte address in the display buffer.</summary>
    public int RowToLinearAddress(int y)
    {
        int ret = LedStart;
        for (var i = 0; i < y; i++)
            ret += Pitch(i);
        return ret;
    }

    /// <summary>Sets a single LED by its linear buffer address.</summary>
    public void SetLedLinear(int address, byte value)
    {
        if (!IsAddressableLed(address))
            return;
        _displayBuffer[address] = value;
    }

    /// <summary>Sets a single LED by planar (x, y) coordinates.</summary>
    public void SetLedPlanar(int x, int y, byte value)
    {
        if (!IsRowInRange(y))
            return;

        if (x >= FirstX(y) && x < Width(y))
            SetLedLinear(RowToLinearAddress(y) - FirstX(y) + x, value);
    }

    /// <summary>Sets a single LED by diagonal coordinates, converting to planar space.</summary>
    public void SetLedDiagonal(int x, int y, byte color, int deltaX = 0, int deltaY = 0)
    {
        x += deltaX;
        y -= deltaY;

        int plX = (x - y) / 2;
        int plY = x + y;

        if (x - y == -1)
            plX = -1;

        SetLedPlanar(plX, plY, color);
    }

    /// <summary>Clears the display buffer. If present is true, flushes to hardware.</summary>
    public void Clear(bool present = false)
    {
        for (var i = 0; i < _displayBuffer.Length; i++)
            _displayBuffer[i] = 0;
        if (present)
            Present();
    }

    /// <summary>Flushes the display buffer to hardware, split into HID pages.</summary>
    public void Present()
    {
        int page = 0;
        int start, end;

        while (page * UpdatePageLength < LedCount)
        {
            start = page * UpdatePageLength;
            end = Math.Min(LedCount, (page + 1) * UpdatePageLength);

            Set(CreatePacket(0xC0, 0x02)
                .AppendData(BitConverter.GetBytes((ushort)(start + 1)))
                .AppendData(BitConverter.GetBytes((ushort)(end - start)))
                .AppendData(_displayBuffer[start..end])
            );

            page++;
        }

        Set(CreatePacket(0xC0, 0x03));
    }

    /// <summary>Clears all stored animation frames and resets the frame index.</summary>
    public void ClearFrames()
    {
        frames.Clear();
        frameIndex = 0;
    }

    /// <summary>Takes a snapshot of the current display buffer as a new animation frame.</summary>
    public void AddFrame()
    {
        frames.Add(_displayBuffer.ToArray());
    }

    /// <summary>Loads the next frame into the display buffer and flushes. Wraps at the end.</summary>
    public void PresentNextFrame()
    {
        if (frameIndex >= frames.Count)
            frameIndex = 0;
        _displayBuffer = frames[frameIndex];
        Present();
        frameIndex++;
    }

    /// <summary>Draws one bar of a 20-bar audio visualiser, using planar or diagonal layout.</summary>
    public void DrawBar(int pos, double h)
    {
        switch (_model)
        {
            case AnimeType.STRIX:
                DrawBarDiagonal(pos, h);
                break;
            default:
                DrawBarPlanar(pos, h);
                break;
        }
    }

    /// <summary>Draws an audio bar in planar coordinate space.</summary>
    public void DrawBarPlanar(int pos, double h)
    {
        int dx = pos * 2;
        int dy = 20;

        for (int y = 0; y < h - (h % 2); y++)
            for (int x = 0; x < 2 - (y % 2); x++)
            {
                SetLedPlanar(x + dx, dy + y, (byte)(h * 255 / 30));
                SetLedPlanar(x + dx, dy - y, 255);
            }
    }

    /// <summary>Draws an audio bar in diagonal coordinate space.</summary>
    public void DrawBarDiagonal(int pos, double h)
    {
        int dx = pos * 2;
        int dy = 0;

        for (int y = 0; y < h / 2; y++)
            for (int x = 0; x < 2; x++)
            {
                byte color = (byte)(Math.Min(1, (h - y - 2) * 2) * 255);
                SetLedDiagonal(x + dx, dy - y, (byte)(h * 255 / 30), 10, -(FullRows / 2));
            }
    }

    /// <summary>Generates a frame from raw grayscale pixel data in planar layout.</summary>
    public void GenerateFrame(byte[] pixels, int width, int height, int contrast = 100, int gamma = 0)
    {
        int targetWidth = MaxColumns * 2;

        Clear();

        for (int y = 0; y < Math.Min(height, MaxRows); y++)
        {
            for (int x = 0; x < Math.Min(width, targetWidth); x++)
            {
                if (x % 2 == y % 2)
                {
                    int idx = y * width + x;
                    if (idx >= pixels.Length)
                        continue;

                    int color = Math.Min((pixels[idx] + gamma) * contrast / 100, 255);
                    if (color > 20)
                        SetLedPlanar(x / 2, y, (byte)color);
                }
            }
        }
    }

    /// <summary>Generates a frame from raw grayscale pixel data in diagonal layout.</summary>
    public void GenerateFrameDiagonal(byte[] pixels, int width, int height,
        int deltaX = 0, int deltaY = 0, int contrast = 100, int gamma = 0)
    {
        Clear();

        for (int y = 0; y < Math.Min(height, MaxRows + FullRows); y++)
        {
            for (int x = 0; x < Math.Min(width, MaxRows + FullRows); x++)
            {
                int idx = y * width + x;
                if (idx >= pixels.Length)
                    continue;

                int color = Math.Min((pixels[idx] + gamma) * contrast / 100, 255);
                if (color > 20)
                    SetLedDiagonal(x, y, (byte)color, deltaX, height + deltaY - (FullRows / 2) - 1);
            }
        }
    }

    /// <summary>Detects the AnimeMatrix model based on the laptop product name.</summary>
    public static AnimeType DetectModel()
    {
        if (AppConfig.ContainsModel("401"))
            return AnimeType.GA401;
        if (AppConfig.ContainsModel("GU604"))
            return AnimeType.GU604;
        if (AppConfig.ContainsModel("G635") || AppConfig.ContainsModel("G615") ||
            AppConfig.ContainsModel("G835") || AppConfig.ContainsModel("G815"))
            return AnimeType.STRIX;
        return AnimeType.GA402;
    }

    public static bool IsAnimeMatrix() => AppConfig.IsAnimeMatrix();

    private bool IsRowInRange(int row)
    {
        return row >= 0 && row < MaxRows;
    }

    private bool IsAddressableLed(int address)
    {
        return address >= 0 && address < LedCount;
    }
}
