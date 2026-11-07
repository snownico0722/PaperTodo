using System.Buffers.Binary;

namespace PaperTodo;

// Read chunk lengths and small format headers; never inspect/inflate image data.
internal readonly record struct NoteImagePngInfo(int PayloadBytes, byte BitDepth, byte ColorType,
    int PaletteColors, bool HasAlpha, bool Interlaced)
{
    internal static NoteImagePngInfo Read(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < 33 || !bytes[..8].SequenceEqual(signature)) return default;
        if (BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(8, 4)) != 13 ||
            !bytes.Slice(12, 4).SequenceEqual("IHDR"u8)) return default;
        var depth = bytes[24];
        var type = bytes[25];
        var validDepth = type switch
        {
            0 => depth is 1 or 2 or 4 or 8 or 16,
            2 or 4 or 6 => depth is 8 or 16,
            3 => depth is 1 or 2 or 4 or 8,
            _ => false
        };
        if (!validDepth || bytes[26] != 0 || bytes[27] != 0 || bytes[28] > 1) return default;
        var alpha = type is 4 or 6;
        var colors = 0;
        var payload = 0;
        var offset = 33;
        // A generous chunk-count bound keeps pathological fragmentation out of the UI hint.
        for (var chunks = 0; chunks < 1024 && bytes.Length - offset >= 12; chunks++)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            if (length > bytes.Length - offset - 12) return default;
            var kind = bytes.Slice(offset + 4, 4);
            if (kind.SequenceEqual("IDAT"u8)) payload += (int)length;
            else if (kind.SequenceEqual("PLTE"u8))
            {
                if (length == 0 || length > 768 || length % 3 != 0) return default;
                colors = (int)length / 3;
            }
            else if (kind.SequenceEqual("tRNS"u8)) alpha = true;
            else if (kind.SequenceEqual("acTL"u8)) return default; // Animation was not calibrated.
            else if (kind.SequenceEqual("IEND"u8))
                return length == 0 && payload > 0 && (type != 3 || colors > 0)
                    ? new(payload, depth, type, colors, alpha, bytes[28] != 0) : default;
            offset += (int)length + 12;
        }
        return default;
    }
}
