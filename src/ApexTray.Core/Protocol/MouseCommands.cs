using System.Buffers.Binary;

namespace ApexTray.Core.Protocol;

// RongYuan mouse feature commands (z9r class in the vendor bundle). Byte 7 is the checksum,
// which iot_driver fills in for ChecksumType.Bit7, so builders leave it zero.
public static class MouseCommands
{
    public const int PacketLength = 64;
    public const int MatrixSlots = 16;
    public const int DpiStageCount = 8;

    public const byte GetFirmware = 0x80;
    public const byte GetProfile = 0x85;
    public const byte SetKey = 0x50;
    public const byte SetOption0 = 0x53;
    public const byte SetOption1 = 0x54;
    public const byte GetKeyMatrix = 0xD0;
    public const byte GetOption0 = 0xD3;
    public const byte GetOption1 = 0xD4;

    public static readonly IReadOnlyList<int> PollingRates = [125, 250, 500, 1000, 2000, 4000, 8000];

    private const byte RippleControlFlag = 1 << 2;

    // The vendor app rebuilds option 0 from these fields and sends zeros everywhere else
    // (byte 11, 17-23, 48-49, 61-63), so only these ranges are carried over from a read.
    private static readonly (int Start, int End)[] Option0Fields = [(8, 11), (12, 17), (24, 48), (50, 61)];

    public static byte[] Simple(byte command, params byte[] args)
    {
        var p = new byte[PacketLength];
        p[0] = command;
        args.CopyTo(p, 1);
        return p;
    }

    public static byte[] SetButton(byte profile, int slot, ButtonAction action)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, MatrixSlots);
        var p = new byte[PacketLength];
        p[0] = SetKey;
        p[1] = profile;
        p[2] = (byte)slot;
        action.CopyTo(p.AsSpan(8, 4));
        return p;
    }

    // The 0xD0 reply is the raw 16×4 matrix with no header.
    public static ButtonAction[] ParseMatrix(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < MatrixSlots * 4) throw new InvalidDataException($"Key matrix reply too short ({reply.Length} bytes).");
        var actions = new ButtonAction[MatrixSlots];
        for (var i = 0; i < MatrixSlots; i++)
            actions[i] = ButtonAction.FromBytes(reply.Slice(i * 4, 4));
        return actions;
    }

    public static FirmwareVersion ParseFirmware(ReadOnlySpan<byte> reply) => new(BinaryPrimitives.ReadUInt16LittleEndian(reply[1..]));

    // Stages are always 8 long; a 0 marks a stage that is switched off.
    public static DpiSettings ParseOption1(ReadOnlySpan<byte> reply)
    {
        var stages = new int[DpiStageCount];
        for (var i = 0; i < DpiStageCount; i++)
            stages[i] = BinaryPrimitives.ReadUInt16LittleEndian(reply.Slice(8 + i * 2));
        return new DpiSettings(stages, Math.Clamp((int)reply[2], 0, DpiStageCount - 1));
    }

    // X and Y stages are written identically (the APEX has no separate Y DPI) and the stage colours
    // are copied from current, the matching 0xD4 reply.
    public static byte[] BuildOption1(byte profile, DpiSettings wanted, ReadOnlySpan<byte> current)
    {
        if (wanted.Stages.Length != DpiStageCount) throw new ArgumentException($"Exactly {DpiStageCount} DPI stages are required.", nameof(wanted));
        if (wanted.Stages.Any(s => s != 0 && !Aj179Apex.IsValidDpi(s)))
            throw new ArgumentException($"DPI values must be {Aj179Apex.MinDpi}-{Aj179Apex.MaxDpi} in steps the sensor supports.", nameof(wanted));
        if (wanted.ActiveIndex is < 0 or >= DpiStageCount || wanted.Stages[wanted.ActiveIndex] == 0)
            throw new ArgumentException("The active DPI stage must be switched on.", nameof(wanted));

        var p = new byte[PacketLength];
        p[0] = SetOption1;
        p[1] = profile;
        p[2] = (byte)wanted.ActiveIndex;
        // The vendor app always sends the layout's stage count; the mouse skips stages set to 0.
        p[3] = DpiStageCount;
        for (var i = 0; i < DpiStageCount; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(8 + i * 2), (ushort)wanted.Stages[i]);
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(24 + i * 2), (ushort)wanted.Stages[i]);
        }
        current[40..PacketLength].CopyTo(p.AsSpan(40));
        return p;
    }

    // The stage index from a watchVender notification sent when the DPI button is pressed, otherwise null.
    // Byte 0 is the report id; the vendor app matches bytes 1-3 against [0x0C, stage, 0].
    public static int? ParseDpiSwitch(ReadOnlySpan<byte> notification) =>
        notification.Length >= 4 && notification[1] == 0x0C && notification[3] == 0 && notification[2] < DpiStageCount
            ? notification[2]
            : null;

    // A button on the mouse changed the polling rate; the notification doesn't say to what.
    public static bool IsPollingRateSwitch(ReadOnlySpan<byte> notification) =>
        notification.Length >= 4 && notification[1] == 0x02 && notification[3] == 0;

    public static int? ParsePollingRate(ReadOnlySpan<byte> option0Reply) => option0Reply[9] switch
    {
        0x01 => 1000,
        0x02 => 500,
        0x04 => 250,
        0x08 => 125,
        0x81 => 8000,
        0x82 => 4000,
        0x84 => 2000,
        _ => null,
    };

    public static SensorSettings ParseOption0(ReadOnlySpan<byte> reply) => new(
        ParsePollingRate(reply),
        (LiftOffDistance)Math.Min(reply[52], (byte)LiftOffDistance.High),
        reply[53] != 0,
        (reply[12] & RippleControlFlag) != 0);

    // Option 0 also holds lighting, sleep timers and sensitivity, so it's rebuilt from
    // current (a fresh 0xD3 reply) with only the sensor fields replaced.
    public static byte[] BuildOption0(ReadOnlySpan<byte> current, SensorSettings wanted)
    {
        if (current.Length < PacketLength) throw new ArgumentException("A full 0xD3 reply is required.", nameof(current));
        var p = new byte[PacketLength];
        p[0] = SetOption0;
        foreach (var (start, end) in Option0Fields)
            current[start..end].CopyTo(p.AsSpan(start));

        p[9] = RateCode(wanted.PollingRate ?? ParsePollingRate(current) ?? 1000);
        p[12] = wanted.RippleControl ? (byte)(p[12] | RippleControlFlag) : (byte)(p[12] & ~RippleControlFlag);
        p[52] = (byte)wanted.LiftOff;
        p[53] = wanted.AngleSnapping ? (byte)1 : (byte)0;
        return p;
    }

    private static byte RateCode(int hz) => hz switch
    {
        1000 => 0x01,
        500 => 0x02,
        250 => 0x04,
        125 => 0x08,
        8000 => 0x81,
        4000 => 0x82,
        2000 => 0x84,
        _ => throw new ArgumentOutOfRangeException(nameof(hz), hz, "Unsupported polling rate."),
    };
}

public sealed record DpiSettings(int[] Stages, int ActiveIndex)
{
    public bool SameAs(DpiSettings? other) => other != null && ActiveIndex == other.ActiveIndex && Stages.SequenceEqual(other.Stages);
}

// "Silent height" in the vendor app: how far the mouse can be lifted before tracking stops.
public enum LiftOffDistance : byte
{
    Low = 0,
    Middle = 1,
    High = 2,
}

// AngleSnapping: "Rectilinear correction" in the vendor app.
// RippleControl: "Ripple correction" in the vendor app.
public sealed record SensorSettings(int? PollingRate, LiftOffDistance LiftOff, bool AngleSnapping, bool RippleControl);

// Firmware revision as the mouse reports it; the vendor compares the raw value as hex.
public readonly record struct FirmwareVersion(ushort Raw)
{
    public override string ToString() => $"{Raw >> 8:X}.{Raw & 0xFF:X2}";
}
