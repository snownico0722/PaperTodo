using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PaperTodo;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

// Finite diagnostic consumer on a separate thread, never a presentation/frame producer.
// Only fixture ROI pixels are copied to CPU or retained. This is desktop composition evidence,
// not a claim about the monitor's physical scanout or input-to-photon latency.
internal sealed class DesktopRoiCapture : IDisposable
{
    internal sealed record Sample(long AcquiredQpc, long CopiedQpc, long LastPresentQpc,
        uint AccumulatedFrames, bool ProtectedContentMasked, string Sha256, byte[] Pixels);
    private readonly DeviceScreenRect _roi;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly ManualResetEventSlim _stop = new();
    internal List<Sample> Samples { get; } = new();
    internal string? Error { get; private set; }
    internal string? DeviceName { get; private set; }
    internal long CaptureStartedQpc { get; private set; }
    internal long CaptureEndedQpc { get; private set; }

    internal DesktopRoiCapture(DeviceScreenRect roi)
    {
        _roi = roi;
        _thread = new Thread(Capture) { IsBackground = true, Name = "E013 fixture ROI capture" };
        _thread.Start();
        if (!_ready.Wait(5000))
        {
            _stop.Set();
            // Never leave a successful capture running after constructor failure.
            if (_thread.Join(5000)) { _ready.Dispose(); _stop.Dispose(); }
            throw new TimeoutException("DXGI capture initialization timeout.");
        }
    }

    private unsafe void Capture()
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint ai = 0; factory.EnumAdapters1(ai, out var adapter).Success; ai++)
            using (adapter)
            {
                for (uint oi = 0; adapter.EnumOutputs(oi, out var output).Success; oi++)
                using (output)
                {
                    var d = output.Description;
                    var r = d.DesktopCoordinates;
                    if (!d.AttachedToDesktop || r.Left > _roi.Left || r.Top > _roi.Top ||
                        r.Right < _roi.Right || r.Bottom < _roi.Bottom) continue;
                    if (d.Rotation != ModeRotation.Identity && d.Rotation != ModeRotation.Unspecified)
                        throw new NotSupportedException("Rotated duplication requires explicit coordinate mapping; not silently guessed.");
                    DeviceName = d.DeviceName;
                    var levels = new[] { FeatureLevel.Level_11_0 };
                    var create = D3D11.D3D11CreateDevice(adapter, DriverType.Unknown,
                        DeviceCreationFlags.BgraSupport, levels, out ID3D11Device device, out ID3D11DeviceContext context);
                    using (device)
                    using (context)
                    {
                    create.CheckError();
                    using (var output1 = output.QueryInterface<IDXGIOutput1>())
                    using (var duplication = output1.DuplicateOutput(device))
                    {
                        var desc = new Texture2DDescription(Format.B8G8R8A8_UNorm,
                            (uint)_roi.Width, (uint)_roi.Height, 1, 1,
                            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read);
                        using var staging = device.CreateTexture2D(desc, Span<SubresourceData>.Empty);
                        var box = new Box(_roi.Left-r.Left, _roi.Top-r.Top, 0,
                            _roi.Right-r.Left, _roi.Bottom-r.Top, 1);
                        CaptureStartedQpc = Stopwatch.GetTimestamp();
                        _ready.Set();
                        while (!_stop.IsSet && Stopwatch.GetElapsedTime(CaptureStartedQpc).TotalSeconds < 8 && Samples.Count < 400)
                        {
                            var result = duplication.AcquireNextFrame(50, out var info, out var resource);
                            if (result.Code == unchecked((int)0x887A0027)) continue; // DXGI_ERROR_WAIT_TIMEOUT
                            result.CheckError();
                            var acquired = Stopwatch.GetTimestamp();
                            try
                            {
                                using (resource)
                                {
                                // Cursor-only notifications have no new desktop image; retaining
                                // them can exhaust this finite pixel budget before the handoff.
                                if (info.LastPresentTime == 0) continue;
                                using (var texture = resource.QueryInterface<ID3D11Texture2D>())
                                {
                                    if (texture.Description.Format != Format.B8G8R8A8_UNorm)
                                        throw new NotSupportedException("Unexpected duplication format; no implicit HDR conversion.");
                                    context.CopySubresourceRegion(staging, 0, 0, 0, 0, texture, 0, box);
                                    context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
                                    try
                                    {
                                        var pixels = new byte[checked(_roi.Width * _roi.Height * 4)];
                                        for (var y=0; y<_roi.Height; y++)
                                            Marshal.Copy(mapped.DataPointer + checked(y*(int)mapped.RowPitch),
                                                pixels, y*_roi.Width*4, _roi.Width*4);
                                        Samples.Add(new Sample(acquired, Stopwatch.GetTimestamp(), info.LastPresentTime,
                                            info.AccumulatedFrames, info.ProtectedContentMaskedOut,
                                            Convert.ToHexString(SHA256.HashData(pixels)), pixels));
                                    }
                                    finally { context.Unmap(staging, 0); }
                                }
                                }
                            }
                            finally { duplication.ReleaseFrame().CheckError(); }
                        }
                    }
                    }
                    return;
                }
            }
            throw new InvalidOperationException("No attached DXGI output fully contains the fixture ROI.");
        }
        catch (Exception ex) { Error = ex.ToString(); }
        finally { CaptureEndedQpc=Stopwatch.GetTimestamp(); _ready.Set(); }
    }

    internal void Stop()
    {
        _stop.Set();
        if (!_thread.Join(5000)) throw new TimeoutException("DXGI capture thread did not stop.");
    }

    public void Dispose()
    {
        Stop();
        _ready.Dispose(); _stop.Dispose();
    }
}
