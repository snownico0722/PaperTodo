namespace PaperTodo;

// Header-only hints. Unknown/non-DCT encodings retain the original coarse estimate.
internal readonly record struct NoteImageJpegInfo(int PayloadBytes, double Quantization, double Sampling)
{
    internal static NoteImageJpegInfo Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8) return default;
        Span<double> tables = stackalloc double[4];
        tables.Clear();
        var table = -1;
        var sampling = 0d;
        var position = 2;
        // Bound header work; large APP/ICC payloads are skipped by their lengths.
        for (var markers = 0; markers < 128 && position + 2 <= bytes.Length; markers++)
        {
            if (bytes[position++] != 0xff) return default;
            var marker = bytes[position++];
            if (marker == 0xff) { position--; continue; }
            if (marker is 0 or 0xd8 or 0xd9) return default;
            if (marker is 0x01 or >= 0xd0 and <= 0xd7) continue;
            if (position + 2 > bytes.Length) return default;
            var length = (bytes[position] << 8) | bytes[position + 1];
            if (length < 2 || length > bytes.Length - position) return default;
            if (marker == 0xda)
                return table >= 0 && tables[table] > 0 && sampling > 0
                    ? new(bytes.Length - position, tables[table], sampling) : default;
            var segment = bytes.Slice(position + 2, length - 2);
            position += length;
            if (marker == 0xdb)
            {
                var offset = 0;
                while (offset < segment.Length)
                {
                    var descriptor = segment[offset++];
                    var precision = descriptor >> 4;
                    var id = descriptor & 15;
                    if (precision > 1 || id > 3 || segment.Length - offset < 64 * (precision + 1)) return default;
                    var sum = 0d;
                    for (var i = 0; i < 64; i++)
                    {
                        var value = (int)segment[offset++];
                        if (precision == 1) value = (value << 8) | segment[offset++];
                        if (value == 0) return default;
                        sum += value;
                    }
                    tables[id] = sum / 64;
                }
            }
            else if (marker is 0xc0 or 0xc1 or 0xc2)
            {
                // The fitted model covers 8-bit, three-component JPEGs only.
                if (segment.Length < 15 || segment[0] != 8 || segment[5] != 3) return default;
                table = segment[8];
                if (table > 3) return default;
                var luma = (segment[7] >> 4) * (segment[7] & 15);
                var chroma = Math.Max((segment[10] >> 4) * (segment[10] & 15),
                    (segment[13] >> 4) * (segment[13] & 15));
                if (luma == 0 || chroma == 0) return default;
                sampling = luma / (double)chroma;
            }
        }
        return default;
    }
}
