#if PAPERTODO_MICA_CONTROLLER_EXPERIMENT
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace PaperTodo;

/// <summary>
/// PR #301 experiment only. Hosts Windows App SDK MicaController behind the existing
/// NativeMicaBackdrop owner. It never changes Window.Opacity or foreground WPF visuals.
/// </summary>
internal sealed class AdjustableMicaControllerBackdrop : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        internal int Size;
        internal int ThreadType;
        internal int ApartmentType;
    }

    [ComImport]
    [Guid("29E691FA-4567-4DCA-B319-D0F207EB6807")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    {
        [PreserveSig]
        int CreateDesktopWindowTarget(
            IntPtr hwndTarget,
            [MarshalAs(UnmanagedType.Bool)] bool isTopmost,
            out IntPtr result);

        [PreserveSig]
        int EnsureOnThread(int threadId);
    }

    [DllImport("CoreMessaging.dll", EntryPoint = "CreateDispatcherQueueController",
        CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CreateDispatcherQueueController(
        DispatcherQueueOptions options,
        out IntPtr dispatcherQueueController);

    private global::Windows.System.DispatcherQueueController? _dispatcherQueueController;
    private Compositor? _compositor;
    private DesktopWindowTarget? _target;
    private MicaController? _controller;
    private SystemBackdropConfiguration? _configuration;
    private IntPtr _hwnd;
    private bool _dark;
    private string _level = MaterialTransparencyLevels.Medium;

    internal bool IsActive { get; private set; }
    internal string LastStage { get; private set; } = "not-started";
    internal string? LastError { get; private set; }
    internal float DefaultTintOpacity { get; private set; }
    internal float DefaultLuminosityOpacity { get; private set; }
    internal float AppliedTintOpacity { get; private set; }
    internal float AppliedLuminosityOpacity { get; private set; }

    internal bool TryApply(IntPtr hwnd, bool dark, string? level, bool inputActive)
    {
        level = MaterialTransparencyLevels.Normalize(level);
        LastError = null;
        LastStage = "support-check";
        try
        {
            if (!MicaController.IsSupported())
            {
                LastError = "MicaController.IsSupported returned false.";
                Disable();
                return false;
            }

            if (_controller == null || hwnd != _hwnd || dark != _dark || level != _level)
            {
                if (!CreateController(hwnd, dark, level, inputActive))
                    return false;
            }
            else
            {
                SetInputActive(inputActive);
            }

            IsActive = _controller != null;
            if (IsActive) LastStage = "active";
            return IsActive;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TypeLoadException or
                                   DllNotFoundException or EntryPointNotFoundException or NotSupportedException)
        {
            LastError = $"{ex.GetType().Name} HRESULT=0x{Marshal.GetHRForException(ex):X8}: {ex.Message}";
            Debug.WriteLine($"MicaController experiment unavailable at {LastStage}; using DWM Mica: {LastError}");
            Disable();
            return false;
        }
    }

    internal void SetInputActive(bool active)
    {
        if (_configuration != null) _configuration.IsInputActive = active;
    }

    internal void Disable()
    {
        IsActive = false;
        _controller?.Dispose();
        _controller = null;
        _configuration = null;
        _hwnd = IntPtr.Zero;
    }

    private bool CreateController(IntPtr hwnd, bool dark, string level, bool inputActive)
    {
        Disable();
        LastStage = "composition-target";
        EnsureCompositionTarget(hwnd);

        LastStage = "configuration";
        var configuration = new SystemBackdropConfiguration
        {
            IsInputActive = inputActive,
            IsHighContrast = false,
            Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light
        };
        LastStage = "controller-create";
        var controller = new MicaController { Kind = MicaKind.Base };
        controller.SetSystemBackdropConfiguration(configuration);

        LastStage = "set-target";
        if (!controller.SetTarget(Win32Interop.GetWindowIdFromWindow(hwnd), _target!))
        {
            LastError = "MicaController.SetTarget returned false.";
            controller.Dispose();
            return false;
        }

        DefaultTintOpacity = controller.TintOpacity;
        DefaultLuminosityOpacity = controller.LuminosityOpacity;
        var scale = (float)MaterialTransparencyLevels.CoverScale(level);
        AppliedTintOpacity = Math.Clamp(DefaultTintOpacity * scale, 0f, 1f);
        AppliedLuminosityOpacity = Math.Clamp(DefaultLuminosityOpacity * scale, 0f, 1f);

        if (level != MaterialTransparencyLevels.Medium)
        {
            controller.TintOpacity = AppliedTintOpacity;
            controller.LuminosityOpacity = AppliedLuminosityOpacity;
        }

        LastStage = "apply-opacity";
        _configuration = configuration;
        _controller = controller;
        _hwnd = hwnd;
        _dark = dark;
        _level = level;
        IsActive = true;
        return true;
    }

    private void EnsureCompositionTarget(IntPtr hwnd)
    {
        EnsureDispatcherQueue();
        if (_compositor == null)
            _compositor = new Compositor();

        if (_target != null && _hwnd == hwnd) return;
        (_target as IDisposable)?.Dispose();

        var interop = _compositor.As<ICompositorDesktopInterop>();
        // Match the published WPF/Win32 Windows App SDK interop sample exactly at the ABI:
        // preserve HRESULT, receive the raw IInspectable pointer, then project with FromAbi.
        // This avoids relying on COM marshalling a WinRT class through an out parameter.
        // The backdrop must sit behind WPF's redirected content. A topmost target covers
        // the retained WPF scene and turns the whole paper into an opaque Mica slab.
        var hr = interop.CreateDesktopWindowTarget(hwnd, false, out var targetAbi);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        if (targetAbi == IntPtr.Zero)
            throw new COMException("CreateDesktopWindowTarget returned a null target.", hr);
        _target = DesktopWindowTarget.FromAbi(targetAbi);
        _target.Root = _compositor.CreateContainerVisual();
    }

    private void EnsureDispatcherQueue()
    {
        if (global::Windows.System.DispatcherQueue.GetForCurrentThread() != null) return;
        if (_dispatcherQueueController != null) return;

        var options = new DispatcherQueueOptions
        {
            Size = Marshal.SizeOf<DispatcherQueueOptions>(),
            ThreadType = 2,    // DQTYPE_THREAD_CURRENT
            ApartmentType = 0  // DQTAT_COM_NONE; matches the published WPF interop sample
        };
        LastStage = "dispatcher-queue";
        var hr = CreateDispatcherQueueController(options, out var controllerAbi);
        if (hr != 0) Marshal.ThrowExceptionForHR(unchecked((int)hr));
        if (controllerAbi == IntPtr.Zero)
            throw new COMException("CreateDispatcherQueueController returned null.", unchecked((int)hr));
        try
        {
            _dispatcherQueueController =
                global::Windows.System.DispatcherQueueController.FromAbi(controllerAbi);
        }
        finally
        {
            Marshal.Release(controllerAbi);
        }
    }

    public void Dispose()
    {
        Disable();
        (_target as IDisposable)?.Dispose();
        _target = null;
        (_compositor as IDisposable)?.Dispose();
        _compositor = null;
        _dispatcherQueueController?.ShutdownQueueAsync();
        _dispatcherQueueController = null;
    }
}
#endif
