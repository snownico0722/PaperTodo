using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using PaperTodo;
using SharpGen.Runtime;
using Vortice.DirectComposition;

internal static partial class Program
{
    // Passing input is not sufficient: the cover must still present its live source pixels.
    private sealed class ProxyVisualEvidence : IDisposable
    {
        private readonly Window _source = new()
        {
            Left = 0, Top = 0, Width = 64, Height = 64,
            WindowStyle = WindowStyle.None, AllowsTransparency = true,
            Background = Brushes.Red, ShowInTaskbar = false, Topmost = true,
            ShowActivated = false
        };
        private readonly DeviceScreenRect _bounds;
        private IDCompositionDesktopDevice? _device;
        private IDCompositionTarget? _target;
        private IDCompositionVisual2? _visual;
        private IUnknown? _surface;

        internal ProxyVisualEvidence(IntPtr output, DeviceScreenRect bounds)
        {
            _bounds = bounds;
            try
            {
                _source.Show();
                _source.UpdateLayout();
                Check(WindowNative.TryGetWindowDeviceBounds(_source, out var sourceBounds),
                    "Read real layered source bounds for compositor evidence");
                var iid = typeof(IDCompositionDesktopDevice).GUID;
                Marshal.ThrowExceptionForHR(CreateProxyEvidenceDevice(IntPtr.Zero, ref iid, out var pointer));
                _device = new IDCompositionDesktopDevice(pointer);
                _device.CreateTargetForHwnd(output, true, out _target).CheckError();
                _device.CreateSurfaceFromHwnd(new WindowInteropHelper(_source).Handle, out _surface).CheckError();
                _device.CreateVisual(out IDCompositionVisual2 visual).CheckError();
                _visual = visual;
                visual.SetContent(_surface).CheckError();
                visual.SetOffsetX((bounds.Width - sourceBounds.Width) / 2f).CheckError();
                visual.SetOffsetY((bounds.Height - sourceBounds.Height) / 2f).CheckError();
                _target.SetRoot(visual).CheckError();
                _device.Commit().CheckError();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal void AssertRedAndCloakSource()
        {
            AssertColor(red: true, "Live compositor pixels appear on the output HWND");
            Check(WindowNative.TrySetWindowCloaked(new WindowInteropHelper(_source).Handle, true),
                "Cloak real source while compositor owns visible pixels");
            AssertColor(red: true, "Output retains its visible source after real HWND is cloaked");
        }

        internal void AssertLivePassthrough()
        {
            _source.Background = Brushes.Lime;
            AssertColor(red: false, "Passthrough cover still presents live updated source pixels");
        }

        private void AssertColor(bool red, string message)
        {
            uint last = uint.MaxValue;
            var matched = WaitForProxyCheck(() =>
            {
                _ = FlushProxyEvidenceDesktop();
                var dc = GetProxyEvidenceDC(IntPtr.Zero);
                try { last = GetProxyEvidencePixel(dc, _bounds.Left + _bounds.Width / 2, _bounds.Top + _bounds.Height / 2); }
                finally { ReleaseProxyEvidenceDC(IntPtr.Zero, dc); }
                var r = last & 255;
                var g = (last >> 8) & 255;
                var b = (last >> 16) & 255;
                return red ? r > 200 && g < 40 && b < 40 : r < 40 && g > 200 && b < 40;
            });
            Check(matched, message + $" (desktop pixel 0x{last:X8})");
        }

        public void Dispose()
        {
            try
            {
                WindowNative.TrySetWindowCloaked(new WindowInteropHelper(_source).Handle, false);
                try { _target?.SetRoot(null!); _device?.Commit(); } catch { }
                _visual?.Dispose();
                _surface?.Dispose();
                _target?.Dispose();
                _device?.Dispose();
            }
            finally { _source.Close(); }
        }
    }

    [DllImport("dcomp.dll", EntryPoint = "DCompositionCreateDevice2", ExactSpelling = true)]
    private static extern int CreateProxyEvidenceDevice(IntPtr renderingDevice, ref Guid iid, out IntPtr device);
    [DllImport("dwmapi.dll", EntryPoint = "DwmFlush")]
    private static extern int FlushProxyEvidenceDesktop();
    [DllImport("user32.dll", EntryPoint = "GetDC")]
    private static extern IntPtr GetProxyEvidenceDC(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "ReleaseDC")]
    private static extern int ReleaseProxyEvidenceDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll", EntryPoint = "GetPixel")]
    private static extern uint GetProxyEvidencePixel(IntPtr dc, int x, int y);
}
