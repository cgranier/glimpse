namespace Glimpse.Core.Meaning;

/// <summary>
/// Sets <c>accuracy_level</c> on every MatMulNBits node of an ONNX graph file (only the small graph file; the
/// weights live in a separate data file and aren't touched).
/// <para>
/// The int8 EmbeddingGemma export leaves it unset, which makes ONNX Runtime compute in float32 and dequantize
/// every weight on every run: ~350 ms per query, whatever its length. Level 4 (int8 compute) uses the fast
/// path: ~35 ms, with the same results (cosine 0.999 against the float32 reference).
/// </para>
/// A minimal protobuf rewrite: ModelProto.graph (7) → GraphProto.node (1) → NodeProto.op_type (4) /
/// attribute (5) → AttributeProto name (1), i (3), type (20). Everything else is copied byte for byte.
/// </summary>
public static class OnnxPatch
{
    public static byte[] SetMatMulNBitsAccuracyLevel(byte[] model, int level) =>
        Rewrite(model, (field, value) => field == 7 ? Rewrite(value, (f, node) => f == 1 ? PatchNode(node, level) : null) : null);

    static byte[]? PatchNode(byte[] node, int level)
    {
        var fields = Fields(node).ToList();
        var opType = fields.FirstOrDefault(f => f.Field == 4 && f.Wire == 2);
        if (opType.Value is null || System.Text.Encoding.UTF8.GetString(opType.Value) != "MatMulNBits") return null;

        using var output = new MemoryStream();
        foreach (var f in fields)
        {
            if (f.Field == 5 && f.Wire == 2 && AttributeName(f.Value!) == "accuracy_level") continue; // replaced below
            output.Write(f.Raw);
        }
        using var attribute = new MemoryStream();
        WriteBytes(attribute, 1, "accuracy_level"u8.ToArray()); // name
        WriteVarint(attribute, (3 << 3) | 0);                   // i
        WriteVarint(attribute, (ulong)level);
        WriteVarint(attribute, (20 << 3) | 0);                  // type = INT
        WriteVarint(attribute, 2);
        WriteBytes(output, 5, attribute.ToArray());
        return output.ToArray();
    }

    static string? AttributeName(byte[] attribute) =>
        Fields(attribute).Where(f => f.Field == 1 && f.Wire == 2).Select(f => System.Text.Encoding.UTF8.GetString(f.Value!)).FirstOrDefault();

    /// <summary>Copies a message, replacing length-delimited fields for which <paramref name="replace"/> returns new bytes.</summary>
    static byte[] Rewrite(byte[] message, Func<int, byte[], byte[]?> replace)
    {
        using var output = new MemoryStream(message.Length + 4096);
        foreach (var f in Fields(message))
        {
            if (f.Wire == 2 && replace(f.Field, f.Value!) is { } replaced) WriteBytes(output, f.Field, replaced);
            else output.Write(f.Raw);
        }
        return output.ToArray();
    }

    readonly record struct Entry(int Field, int Wire, byte[]? Value, byte[] Raw);

    static IEnumerable<Entry> Fields(byte[] message)
    {
        var pos = 0;
        while (pos < message.Length)
        {
            var start = pos;
            var tag = ReadVarint(message, ref pos);
            int field = (int)(tag >> 3), wire = (int)(tag & 7);
            byte[]? value = null;
            switch (wire)
            {
                case 0: ReadVarint(message, ref pos); break;
                case 1: pos += 8; break;
                case 5: pos += 4; break;
                case 2:
                    var length = (int)ReadVarint(message, ref pos);
                    value = message.AsSpan(pos, length).ToArray();
                    pos += length;
                    break;
                default: throw new InvalidDataException($"unsupported protobuf wire type {wire}");
            }
            yield return new Entry(field, wire, value, message[start..pos]);
        }
    }

    static ulong ReadVarint(byte[] data, ref int pos)
    {
        ulong result = 0;
        for (var shift = 0; ; shift += 7)
        {
            var b = data[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80) return result;
        }
    }

    static void WriteVarint(Stream s, ulong value)
    {
        while (value >= 0x80)
        {
            s.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        s.WriteByte((byte)value);
    }

    static void WriteBytes(Stream s, int field, byte[] value)
    {
        WriteVarint(s, ((ulong)field << 3) | 2);
        WriteVarint(s, (ulong)value.Length);
        s.Write(value);
    }
}
