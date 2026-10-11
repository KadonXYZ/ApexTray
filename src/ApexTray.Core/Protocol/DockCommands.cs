using System.Buffers.Binary;

namespace ApexTray.Core.Protocol;

// Commands for the 128×128 RGB565 screen on the dock (qpa class in the vendor bundle).
// Sent with sendRawFeature, Bit7 checksum, DangleDevType.None.
public static class DockCommands
{
    public const int ScreenSize = 128;
    public const int ChunkSize = 56;
    public const int ImageSlots = 5;

    public const byte ImageHeader = 0xA5;
    public const byte ImageData = 0x25;
    public const byte Clock = 0x28;
    public const byte FlashErase = 0xAC;
    public const byte Version = 0xAD;

    // Announces an image transfer; the dock acknowledges with reply[1] == 1.
    public static byte[] Header(byte frame, byte frameCount, byte delayMs, PixelRect box, int byteLength)
    {
        var p = new byte[18];
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)byteLength);
        p[0] = ImageHeader;
        p[1] = frame;
        p[2] = frameCount;
        p[3] = delayMs;
        p[4] = len[0];
        p[5] = len[1];
        p[8] = (byte)box.Left;
        p[9] = (byte)box.Top;
        p[10] = (byte)box.Right;
        p[11] = (byte)box.Bottom;
        p[12] = (byte)(box.Left >> 8);
        p[13] = (byte)(box.Top >> 8);
        p[14] = (byte)(box.Right >> 8);
        p[15] = (byte)(box.Bottom >> 8);
        p[16] = len[2];
        p[17] = len[3];
        return p;
    }

    // Splits one frame's pixel data into 0x25 packets; chunk numbering restarts per frame.
    public static IEnumerable<byte[]> DataPackets(byte frame, byte frameCount, byte delayMs, byte[] pixels)
    {
        var chunks = (pixels.Length + ChunkSize - 1) / ChunkSize;
        for (var i = 0; i < chunks; i++)
        {
            var valid = Math.Min(ChunkSize, pixels.Length - i * ChunkSize);
            var p = new byte[8 + ChunkSize];
            p[0] = ImageData;
            p[1] = frame;
            p[2] = frameCount;
            p[3] = delayMs;
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(4), (ushort)i);
            p[6] = (byte)valid;
            Array.Copy(pixels, i * ChunkSize, p, 8, valid);
            yield return p;
        }
    }

    public static byte[] SetClock(DateTime now)
    {
        var p = new byte[64];
        p[0] = Clock;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(8), (ushort)now.Year);
        p[10] = (byte)now.Month;
        p[11] = (byte)now.Day;
        p[12] = (byte)now.Hour;
        p[13] = (byte)now.Minute;
        p[14] = (byte)now.Second;
        return p;
    }

    public static byte[] Simple(byte command)
    {
        var p = new byte[64];
        p[0] = command;
        return p;
    }

    public static bool IsEraseAck(ReadOnlySpan<byte> reply)
        => reply.Length >= 5 && reply[0] == FlashErase && reply[1] == 0xAA && reply[2] == 0xAA && reply[3] == 0x55 && reply[4] == 0x55;

    public static string ParseVersion(ReadOnlySpan<byte> reply)
        => reply.Length >= 3 ? $"{BinaryPrimitives.ReadUInt16LittleEndian(reply[1..]):X4}" : "?";
}

// Pixel bounds with exclusive Right/Bottom, matching the vendor's bounding-box convention.
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}
