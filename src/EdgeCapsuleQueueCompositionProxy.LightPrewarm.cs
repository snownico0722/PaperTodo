using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SharpGen.Runtime;
using Vortice.DirectComposition;

namespace PaperTodo;

internal sealed partial class EdgeCapsuleQueueCompositionProxy
{
    private const int LightweightWpfSourceCount = 2;
    private const int LightweightWpfWidth = 32;
    private const int LightweightWpfHeight = 24;
    private const int LightweightWpfShift = 4;

    private sealed class LightweightPrewarmState
    {
        public bool Attempted { get; set; }
    }

    private sealed class LightweightWpfSource : IDisposable
    {
        private bool _disposed;

        private LightweightWpfSource(
            Window window,
            Border root,
            IntPtr handle)
        {
            Window = window;
            Root = root;
            Handle = handle;
        }

        public Window Window { get; }
        public Border Root { get; }
        public IntPtr Handle { get; }

        public static LightweightWpfSource Create(DeviceScreenRect bounds)
        {
            var root = new Border
            {
                Width = LightweightWpfWidth,
                Height = LightweightWpfHeight,
                Background = Brushes.White,
                BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(1),
                SnapsToDevicePixels = true
            };
            var window = new Window
            {
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true,
                Topmost = true,
                Left = -32000,
                Top = -32000,
                Width = LightweightWpfWidth,
                Height = LightweightWpfHeight,
                Content = root
            };

            try
            {
                window.Show();
                WindowNative.ApplyNoActivateStyle(window);
                var handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        "The WPF prewarm HWND could not be created.");
                }

                var result = new LightweightWpfSource(
                    window,
                    root,
                    handle);
                if (!WindowNative.TrySetWindowDeviceBounds(window, bounds))
                {
                    result.Dispose();
                    throw new InvalidOperationException(
                        "The WPF prewarm HWND could not be positioned.");
                }
                return result;
            }
            catch
            {
                try { window.Close(); } catch { }
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try { Window.Hide(); } catch { }
            try { Window.Close(); } catch { }
        }
    }

    private static readonly ConditionalWeakTable<
        Dispatcher,
        LightweightPrewarmState> LightweightPrewarmStates = new();

    internal static void PrewarmLightweight(Dispatcher dispatcher)
    {
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }
        if (!dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                (Action)(() => PrewarmLightweight(dispatcher)));
            return;
        }

        var state = LightweightPrewarmStates.GetValue(
            dispatcher,
            static _ => new LightweightPrewarmState());
        if (state.Attempted)
        {
            return;
        }
        state.Attempted = true;

        try
        {
            if (!TryGetRuntime(dispatcher, out var runtime))
            {
                return;
            }

            runtime.PrewarmOutputHost();
            RunLightweightPrewarmProbe(runtime, dispatcher);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                "Edge capsule lightweight prewarm failed. Exception={0}",
                ex);
        }
    }

    private static void RunLightweightPrewarmProbe(
        SharedRuntime runtime,
        Dispatcher dispatcher)
    {
        EdgeCapsuleQueueProxyWindow? window = null;
        IDCompositionTarget? target = null;
        IDCompositionVisual2? root = null;
        IDCompositionAnimation? animation = null;
        var wpfSources = new List<LightweightWpfSource>(
            LightweightWpfSourceCount);
        var sourceBounds = new List<DeviceScreenRect>(
            LightweightWpfSourceCount);
        var targetBounds = new List<DeviceScreenRect>(
            LightweightWpfSourceCount);
        var surfaces = new List<IUnknown>(
            LightweightWpfSourceCount);
        var visuals = new List<IDCompositionVisual>(
            LightweightWpfSourceCount);
        var wpfAnimations = new List<IDCompositionAnimation>(
            LightweightWpfSourceCount);

        try
        {
            var offscreen =
                new DeviceScreenRect(-32000, -32000, -31904, -31936);

            window = EdgeCapsuleQueueProxyWindow.TryCreate(
                offscreen,
                topmost: true,
                static _ => false,
                static _ => { },
                static () => { },
                static () => { },
                static () => { });
            if (window == null)
            {
                throw new InvalidOperationException(
                    "The lightweight queue prewarm output window could not be created.");
            }

            runtime.Device.CreateTargetForHwnd(
                window.Handle,
                topmost: true,
                out target).CheckError();

            runtime.Device.CreateVisual(out root).CheckError();
            animation = runtime.Device.CreateAnimation();
            const double animationDurationSeconds = 0.016;
            const float delta = 1f;
            animation.SetAbsoluteBeginTime(
                Stopwatch.GetTimestamp()).CheckError();
            animation.AddCubic(
                0,
                0,
                (float)(3 * delta / animationDurationSeconds),
                (float)(-3 * delta /
                    (animationDurationSeconds * animationDurationSeconds)),
                (float)(delta /
                    (animationDurationSeconds * animationDurationSeconds *
                     animationDurationSeconds))).CheckError();
            animation.End(animationDurationSeconds, delta).CheckError();
            root.SetOffsetX(animation).CheckError();

            target.SetRoot(root).CheckError();
            runtime.Device.Commit().CheckError();
            if (!window.Show(offscreen, topmost: true) ||
                !WindowNative.TryFlushDesktopComposition())
            {
                throw new InvalidOperationException(
                    "The lightweight queue prewarm publication boundary failed.");
            }

            var cloakResult = WindowNative.TrySetWindowCloakedBatchDetailed(
                new[]
                {
                    new WindowNative.WindowCloakChange(
                        window.Handle,
                        Cloaked: true,
                        RollbackCloaked: false)
                });
            if (cloakResult != WindowNative.WindowCloakBatchResult.Success)
            {
                throw new InvalidOperationException(
                    "The lightweight queue prewarm cloak failed.");
            }

            var uncloakResult = WindowNative.TrySetWindowCloakedBatchDetailed(
                new[]
                {
                    new WindowNative.WindowCloakChange(
                        window.Handle,
                        Cloaked: false,
                        RollbackCloaked: true)
                });
            if (uncloakResult != WindowNative.WindowCloakBatchResult.Success)
            {
                throw new InvalidOperationException(
                    "The lightweight queue prewarm uncloak failed.");
            }

            // WPF layered HWNDs have a distinct first-use path from plain Win32 windows.
            // Exercise that path off-screen, then wrap the live HwndSource surfaces in the
            // same translation-only DComp plumbing used by a real queue transaction.
            for (var index = 0; index < LightweightWpfSourceCount; index++)
            {
                var left = offscreen.Left + 8 + index * 40;
                var top = offscreen.Top + 12;
                var bounds = new DeviceScreenRect(
                    left,
                    top,
                    left + LightweightWpfWidth,
                    top + LightweightWpfHeight);
                var moved = new DeviceScreenRect(
                    bounds.Left,
                    bounds.Top + LightweightWpfShift,
                    bounds.Right,
                    bounds.Bottom + LightweightWpfShift);
                wpfSources.Add(LightweightWpfSource.Create(bounds));
                sourceBounds.Add(bounds);
                targetBounds.Add(moved);
            }

            foreach (var source in wpfSources)
            {
                source.Window.UpdateLayout();
                source.Root.UpdateLayout();
            }
            dispatcher.Invoke(
                DispatcherPriority.Render,
                static () => { });
            if (!WindowNative.TryFlushDesktopComposition())
            {
                throw new InvalidOperationException(
                    "The WPF prewarm source render barrier failed.");
            }

            IDCompositionVisual? reference = null;
            for (var index = 0; index < wpfSources.Count; index++)
            {
                runtime.Device.CreateSurfaceFromHwnd(
                    wpfSources[index].Handle,
                    out var surface).CheckError();
                surfaces.Add(surface);

                runtime.Device.CreateVisual(
                    out IDCompositionVisual2 sourceVisual).CheckError();
                visuals.Add(sourceVisual);
                sourceVisual.SetContent(surface).CheckError();
                sourceVisual.SetBitmapInterpolationMode(
                    BitmapInterpolationMode.Linear).CheckError();
                sourceVisual.SetBorderMode(BorderMode.Soft).CheckError();
                sourceVisual.SetOffsetX(
                    sourceBounds[index].Left - offscreen.Left).CheckError();
                sourceVisual.SetOffsetY(
                    sourceBounds[index].Top - offscreen.Top).CheckError();
                root.AddVisual(
                    sourceVisual,
                    insertAbove: true,
                    reference!).CheckError();
                reference = sourceVisual;
            }
            runtime.Device.Commit().CheckError();
            if (!WindowNative.TryFlushDesktopComposition())
            {
                throw new InvalidOperationException(
                    "The WPF redirected source visual cover could not be published.");
            }

            var coordinatedResult =
                WindowNative.TrySetWindowCloakedBatchDetailed(
                    wpfSources
                        .Select(source =>
                            new WindowNative.WindowCloakChange(
                                source.Handle,
                                Cloaked: true,
                                RollbackCloaked: false))
                        .ToArray(),
                    () =>
                    {
                        using (var batch =
                               WindowNative.BeginWindowDeviceBoundsBatch(
                                   wpfSources.Count))
                        {
                            for (var index = 0;
                                 index < wpfSources.Count;
                                 index++)
                            {
                                if (!batch.TryDefer(
                                        wpfSources[index].Handle,
                                        targetBounds[index]))
                                {
                                    return false;
                                }
                            }
                            if (!batch.Commit())
                            {
                                return false;
                            }
                        }

                        foreach (var source in wpfSources)
                        {
                            source.Window.UpdateLayout();
                            source.Root.UpdateLayout();
                        }
                        dispatcher.Invoke(
                            DispatcherPriority.Render,
                            static () => { });

                        var absoluteBeginTimestamp = Stopwatch.GetTimestamp();
                        const double wpfDurationSeconds = 0.016;
                        var wpfDelta = (float)LightweightWpfShift;
                        for (var index = 0;
                             index < visuals.Count;
                             index++)
                        {
                            var from =
                                (float)(sourceBounds[index].Top -
                                    offscreen.Top);
                            var to =
                                (float)(targetBounds[index].Top -
                                    offscreen.Top);
                            var sourceAnimation =
                                runtime.Device.CreateAnimation();
                            wpfAnimations.Add(sourceAnimation);
                            sourceAnimation.SetAbsoluteBeginTime(
                                absoluteBeginTimestamp).CheckError();
                            sourceAnimation.AddCubic(
                                0,
                                from,
                                (float)(3 * wpfDelta /
                                    wpfDurationSeconds),
                                (float)(-3 * wpfDelta /
                                    (wpfDurationSeconds *
                                     wpfDurationSeconds)),
                                (float)(wpfDelta /
                                    (wpfDurationSeconds *
                                     wpfDurationSeconds *
                                     wpfDurationSeconds))).CheckError();
                            sourceAnimation.End(
                                wpfDurationSeconds,
                                to).CheckError();
                            visuals[index]
                                .SetOffsetY(sourceAnimation)
                                .CheckError();
                        }
                        runtime.Device.Commit().CheckError();
                        return true;
                    });
            if (coordinatedResult !=
                WindowNative.WindowCloakBatchResult.Success)
            {
                throw new InvalidOperationException(
                    "The WPF-HWND coordinated prewarm publication failed.");
            }

            var wpfUncloakResult =
                WindowNative.TrySetWindowCloakedBatchDetailed(
                    wpfSources
                        .Select(source =>
                            new WindowNative.WindowCloakChange(
                                source.Handle,
                                Cloaked: false,
                                RollbackCloaked: true))
                        .ToArray());
            if (wpfUncloakResult !=
                WindowNative.WindowCloakBatchResult.Success)
            {
                throw new InvalidOperationException(
                    "The WPF-HWND prewarm sources could not be uncloaked.");
            }
        }
        finally
        {
            try { window?.Hide(); } catch { }
            try
            {
                if (target != null)
                {
                    target.SetRoot(null!).CheckError();
                    runtime.Device.Commit().CheckError();
                }
            }
            catch { }

            for (var index = wpfAnimations.Count - 1;
                 index >= 0;
                 index--)
            {
                try { wpfAnimations[index].Dispose(); } catch { }
            }
            for (var index = visuals.Count - 1;
                 index >= 0;
                 index--)
            {
                try { visuals[index].Dispose(); } catch { }
            }
            for (var index = surfaces.Count - 1;
                 index >= 0;
                 index--)
            {
                try { surfaces[index].Dispose(); } catch { }
            }
            for (var index = wpfSources.Count - 1;
                 index >= 0;
                 index--)
            {
                try { wpfSources[index].Dispose(); } catch { }
            }

            try { animation?.Dispose(); } catch { }
            try { root?.Dispose(); } catch { }
            try { target?.Dispose(); } catch { }
            try { window?.Dispose(); } catch { }
        }
    }
}
