using System.Text;

namespace ApexTray.Core.Transport;

// Just enough protobuf for iot_driver's messages; a full Google.Protobuf + codegen
// dependency isn't worth it for a handful of flat messages.
internal sealed class ProtoWriter
{
    private readonly MemoryStream _ms = new();

    public ProtoWriter String(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

    public ProtoWriter Bytes(int field, ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty) return this;
        Varint((ulong)(field << 3 | 2));
        Varint((ulong)value.Length);
        _ms.Write(value);
        return this;
    }

    public ProtoWriter UInt(int field, ulong value)
    {
        if (value == 0) return this;
        Varint((ulong)(field << 3));
        Varint(value);
        return this;
    }

    public ProtoWriter Bool(int field, bool value) => UInt(field, value ? 1UL : 0UL);

    public byte[] ToArray() => _ms.ToArray();

    private void Varint(ulong v)
    {
        do
        {
            var b = (byte)(v & 0x7F);
            v >>= 7;
            if (v != 0) b |= 0x80;
            _ms.WriteByte(b);
        } while (v != 0);
    }
}

internal readonly record struct ProtoField(int Number, int WireType, ulong Value, ReadOnlyMemory<byte> Data)
{
    public string AsString() => Encoding.UTF8.GetString(Data.Span);
}

internal static class ProtoReader
{
    public static List<ProtoField> Parse(ReadOnlyMemory<byte> buffer)
    {
        var fields = new List<ProtoField>();
        var span = buffer.Span;
        var i = 0;
        while (i < span.Length)
        {
            var key = ReadVarint(span, ref i);
            var number = (int)(key >> 3);
            var wire = (int)(key & 7);
            switch (wire)
            {
                case 0:
                    fields.Add(new ProtoField(number, wire, ReadVarint(span, ref i), default));
                    break;
                case 1:
                    fields.Add(new ProtoField(number, wire, BitConverter.ToUInt64(span.Slice(i, 8)), default));
                    i += 8;
                    break;
                case 2:
                    var len = (int)ReadVarint(span, ref i);
                    fields.Add(new ProtoField(number, wire, 0, buffer.Slice(i, len)));
                    i += len;
                    break;
                case 5:
                    fields.Add(new ProtoField(number, wire, BitConverter.ToUInt32(span.Slice(i, 4)), default));
                    i += 4;
                    break;
                default:
                    throw new InvalidDataException($"Unsupported protobuf wire type {wire}");
            }
        }
        return fields;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> span, ref int i)
    {
        ulong result = 0;
        var shift = 0;
        byte b;
        do
        {
            b = span[i++];
            result |= (ulong)(b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);
        return result;
    }
}
