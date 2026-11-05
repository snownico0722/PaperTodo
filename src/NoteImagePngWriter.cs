using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace PaperTodo;

// RGB PNG scanlines streamed through one zlib stream and bounded IDAT chunks.
// This avoids a full-height WPF bitmap / pixel buffer for long notes.
internal sealed class NoteImagePngWriter : IDisposable
{
    private readonly Stream _output;
    private readonly IdatStream _idat;
    private readonly ZLibStream _zlib;
    private readonly int _width;
    private readonly int _height;
    private readonly byte[] _row;
    private int _rows;
    private bool _completed;

    internal NoteImagePngWriter(Stream output, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        _output = output; _width = width; _height = height;
        _row = new byte[checked(width * 3 + 1)];
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Span<byte> header = stackalloc byte[13];
        header.Clear();
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8; header[9] = 2; // 8-bit RGB, opaque paper background.
        Chunk(output, "IHDR", header);
        Chunk(output, "sRGB", new byte[] { 0 });
        _idat = new IdatStream(output);
        _zlib = new ZLibStream(_idat, CompressionLevel.Optimal, leaveOpen: true);
    }

    internal void WriteBgraRows(byte[] pixels, int count)
    {
        if (_completed || count < 0 || _rows + count > _height || pixels.Length < (long)_width * 4 * count)
            throw new InvalidOperationException("Invalid PNG scanline count.");
        for (var row = 0; row < count; row++)
        {
            _row[0] = 1; // Sub filter; each row is independent of stripe boundaries.
            byte red = 0, green = 0, blue = 0;
            for (var x = 0; x < _width; x++)
            {
                var input = (row * _width + x) * 4;
                var output = x * 3 + 1;
                _row[output] = unchecked((byte)(pixels[input + 2] - red));
                _row[output + 1] = unchecked((byte)(pixels[input + 1] - green));
                _row[output + 2] = unchecked((byte)(pixels[input] - blue));
                red = pixels[input + 2]; green = pixels[input + 1]; blue = pixels[input];
            }
            _zlib.Write(_row);
        }
        _rows += count;
    }

    internal void Complete()
    {
        if (_completed) return;
        if (_rows != _height) throw new InvalidOperationException("PNG ended before the last scanline.");
        _zlib.Dispose();
        _idat.Flush();
        Chunk(_output, "IEND", ReadOnlySpan<byte>.Empty);
        _completed = true;
    }

    public void Dispose() => _zlib.Dispose();

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(value =>
    {
        var crc = (uint)value;
        for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320u ^ (crc >> 1) : crc >> 1;
        return crc;
    }).ToArray();

    private static void Chunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        output.Write(number);
        var tag = Encoding.ASCII.GetBytes(type);
        output.Write(tag); output.Write(data);
        var crc = uint.MaxValue;
        foreach (var b in tag) crc = CrcTable[(crc ^ b) & 255] ^ (crc >> 8);
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 255] ^ (crc >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
        output.Write(number);
    }

    private sealed class IdatStream(Stream output) : Stream
    {
        private readonly byte[] _buffer = new byte[64 * 1024];
        private int _used;
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (!buffer.IsEmpty)
            {
                var count = Math.Min(_buffer.Length - _used, buffer.Length);
                buffer[..count].CopyTo(_buffer.AsSpan(_used));
                _used += count; buffer = buffer[count..];
                if (_used == _buffer.Length) Flush();
            }
        }
        public override void Flush()
        {
            if (_used == 0) return;
            Chunk(output, "IDAT", _buffer.AsSpan(0, _used));
            _used = 0;
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
