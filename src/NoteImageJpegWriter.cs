using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace PaperTodo;

// Stream 24-bit BGR rows to Windows' JPEG codec without a full-height bitmap.
// The caller passes a temporary path and atomically moves it after completion.
internal sealed class NoteImageJpegWriter : IDisposable
{
    internal const int Quality = 90;
    private static readonly Guid JpegContainer = new("19e4a5aa-5662-4fc5-a0c0-1758028e1057");
    private static readonly Guid Bgr24 = new("6fddc324-4e03-4bfe-b185-3d77768dc90c");
    private readonly int _stride;
    private readonly int _height;
    private int _written;
    private bool _completed;
    private IStream? _stream;
    private IWicImagingFactory? _factory;
    private IWicBitmapEncoder? _encoder;
    private IWicBitmapFrameEncode? _frame;
    private IPropertyBag2? _options;

    internal NoteImageJpegWriter(string path, int width, int height)
    {
        if (width <= 0 || height <= 0 || width > ushort.MaxValue || height > ushort.MaxValue)
            throw new NotSupportedException(Strings.Get("EditorImageExportJpegHeightLimit"));
        _stride = checked(width * 3);
        _height = height;
        try
        {
            // STGM_CREATE | STGM_WRITE | STGM_SHARE_EXCLUSIVE.
            Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(path, 0x1011, 0x80,
                true, IntPtr.Zero, out var fileStream));
            _stream = fileStream;
            _factory = (IWicImagingFactory)new WicImagingFactoryClass();
            var container = JpegContainer;
            _factory.CreateEncoder(ref container, IntPtr.Zero, out var encoder);
            _encoder = encoder;
            _encoder.Initialize(_stream, 2); // WICBitmapEncoderNoCache.
            _encoder.CreateNewFrame(out var frame, out var options);
            _frame = frame;
            _options = options;

            // ImageQuality is VT_R4 (0..1). Set 0.90 explicitly.
            var property = new PropertyBag2 { Type = 1, VariantType = 4, Name = "ImageQuality" };
            object quality = Quality / 100f;
            _options.Write(1, ref property, ref quality);
            _frame.Initialize(_options);
            _frame.SetSize((uint)width, (uint)height);
            var pixelFormat = Bgr24;
            _frame.SetPixelFormat(ref pixelFormat);
            if (pixelFormat != Bgr24)
                throw new NotSupportedException("The Windows JPEG codec requires a different pixel format.");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void WriteBgrRows(byte[] pixels, int count)
    {
        if (_completed || count <= 0 || count > _height - _written ||
            pixels.Length < (long)_stride * count)
            throw new InvalidOperationException("Invalid JPEG scanline count.");
        // Pass the pixel address, not a COM-marshaled array descriptor.
        // The latter can produce a valid JPEG header with corrupt image content.
        var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            _frame!.WritePixels((uint)count, (uint)_stride,
                checked((uint)(_stride * count)), pinned.AddrOfPinnedObject());
        }
        finally { pinned.Free(); }
        _written += count;
    }

    internal void Complete()
    {
        if (_completed) return;
        if (_written != _height)
            throw new InvalidOperationException("JPEG ended before the last scanline.");
        _frame!.Commit();
        _encoder!.Commit();
        _completed = true;
    }

    public void Dispose()
    {
        Release(ref _frame);
        Release(ref _options);
        Release(ref _encoder);
        Release(ref _factory);
        Release(ref _stream);
    }

    private static void Release<T>(ref T? instance) where T : class
    {
        if (instance == null) return;
        Marshal.FinalReleaseComObject(instance);
        instance = null;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateStreamOnFileEx(
        string path, uint mode, uint attributes, [MarshalAs(UnmanagedType.Bool)] bool create,
        IntPtr template, out IStream stream);

    [ComImport, Guid("cacaf262-9370-4615-a13b-9f5539da4c0a")]
    private class WicImagingFactoryClass { }

    [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWicImagingFactory
    {
        void CreateDecoderFromFilename();
        void CreateDecoderFromStream();
        void CreateDecoderFromFileHandle();
        void CreateComponentInfo();
        void CreateDecoder();
        void CreateEncoder(ref Guid container, IntPtr vendor, out IWicBitmapEncoder encoder);
    }

    [ComImport, Guid("00000103-a8f2-4877-ba0a-fd2b6645fb94"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWicBitmapEncoder
    {
        void Initialize(IStream stream, uint cacheOption);
        void GetContainerFormat();
        void GetEncoderInfo();
        void SetColorContexts();
        void SetPalette();
        void SetThumbnail();
        void SetPreview();
        void CreateNewFrame(out IWicBitmapFrameEncode frame, out IPropertyBag2 options);
        void Commit();
    }

    [ComImport, Guid("00000105-a8f2-4877-ba0a-fd2b6645fb94"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWicBitmapFrameEncode
    {
        void Initialize(IPropertyBag2 options);
        void SetSize(uint width, uint height);
        void SetResolution(double dpiX, double dpiY);
        void SetPixelFormat(ref Guid pixelFormat);
        void SetColorContexts();
        void SetPalette();
        void SetThumbnail();
        void WritePixels(uint lineCount, uint stride, uint bufferSize, IntPtr pixels);
        void WriteSource();
        void Commit();
    }

    [ComImport, Guid("22f55882-280b-11d0-a8a9-00a0c90c2004"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag2
    {
        void Read();
        void Write(uint count, ref PropertyBag2 property,
            [In, MarshalAs(UnmanagedType.Struct)] ref object value);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PropertyBag2
    {
        public uint Type;
        public ushort VariantType;
        public ushort ClipboardFormat;
        public uint Hint;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Name;
        public Guid ClassId;
    }
}
