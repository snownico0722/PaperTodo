using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Threading;
using PaperTodo;
using SharpGen.Runtime;
using Vortice.DirectComposition;

internal static partial class Program
{
    private static void ProxyAnimationValueProbe(string output)
    {
        output = Path.GetFullPath(output);
        Require(!Directory.Exists(output), "Refusing to overwrite animation evidence");
        Directory.CreateDirectory(output);
        Require(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var screen), "probe monitor");
        var monitor = screen with { WorkArea = new DeviceScreenRect(
            screen.WorkArea.Left + (int)(100 * screen.DpiScaleX),
            screen.WorkArea.Top + (int)(100 * screen.DpiScaleY),
            screen.WorkArea.Right - (int)(100 * screen.DpiScaleX),
            screen.WorkArea.Bottom - (int)(100 * screen.DpiScaleY)) };
        var failures = new List<string>();
        foreach (var nativeShape in new[] { false, true })
        {
            try { ProxyOpacityPixelScenario(output, monitor, EdgeCapsuleEdge.Left, 0, true,
                nativeShape, nativeShape ? "native-shape-on" : "real-surface-control"); }
            catch (Exception ex) { failures.Add($"nativeShape={nativeShape}: {ex}"); }
        }
        Require(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    // Opt-in finite desktop-composition diagnostic over a controlled actual Host. Default checks
    // do not record the desktop. Raw ROI samples are preserved even when a pixel assertion fails.
    private static void ProxyOpacityPixels(string output)
    {
        output = Path.GetFullPath(output);
        Require(!Directory.Exists(output), "Refusing to overwrite pixel evidence");
        Directory.CreateDirectory(output);
        Require(WindowWorkAreaHelper.TryGetMonitorGeometryForDevice(null, out var screen), "pixel monitor");
        var monitor = screen with { WorkArea = new DeviceScreenRect(
            screen.WorkArea.Left + (int)(100 * screen.DpiScaleX),
            screen.WorkArea.Top + (int)(100 * screen.DpiScaleY),
            screen.WorkArea.Right - (int)(100 * screen.DpiScaleX),
            screen.WorkArea.Bottom - (int)(100 * screen.DpiScaleY)) };
        var failures = new List<string>();
        void Scenario(EdgeCapsuleEdge edge, int delay, bool resize)
        {
            try { ProxyOpacityPixelScenario(output, monitor, edge, delay, resize); }
            catch (Exception ex) { failures.Add($"{edge} delay={delay} resize={resize}: {ex}"); }
        }
        foreach (var edge in new[] { EdgeCapsuleEdge.Left, EdgeCapsuleEdge.Right })
        {
            foreach (var delay in new[] { 0, 80 }) Scenario(edge, delay, false);
            Scenario(edge, 0, true);
        }
        Require(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static void ProxyOpacityPixelScenario(string output, MonitorGeometry monitor,
        EdgeCapsuleEdge edge, int faultDelay, bool resize, bool nativeShape = true, string? scenarioName = null)
    {
        var name = scenarioName ?? (edge.ToString().ToLowerInvariant() + "-" +
            (resize ? "preview-open-reverse" : faultDelay == 0 ? "normal-handoff" : "injected-channel-delay"));
        var folder = Path.Combine(output, name);
        Directory.CreateDirectory(folder);
        using var host = NewHost();
        var source = (Window)typeof(EdgeCapsuleHost).GetProperty("Window",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        var layout = new EdgeCapsuleLayoutSnapshot(monitor, edge, 0, 0,
            280, 0, 40, resize ? 360 : 280, resize ? 200 : 40, true, 1, 1, 420, resize ? 208 : 48);
        var presenter = new EdgeCapsulePresenter();
        Require(presenter.Dispatch(EdgeCapsuleIntent.Attach(new(0, 0, 1),
            EdgeCapsulePaperForm.Collapsed, false)).Accepted, "pixel attach");
        var applyCount = 0;
        void Reconcile(EdgeCapsuleDirty dirty) => presenter.Reconcile(dirty, () => layout,
            () => null, frame => frame, frame => { applyCount++; return host.Apply(frame); },
            Stopwatch.GetTimestamp());
        presenter.RequestPresentation(EdgeCapsuleMotion.Snap(EdgeCapsuleTransitionReason.State));
        Reconcile(EdgeCapsuleDirty.Measure | EdgeCapsuleDirty.Presentation);
        source.UpdateLayout();
        if (resize)
        {
            var body = new TextBlock { Text = "路线 3：由 WPF 按最终宽度排版的正文。\n" +
                "Independent live text; no glyph scaling.\n" +
                "展开与收起只改变可见范围和外壳。", FontSize = 16,
                TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Black, Background = Brushes.White,
                Width = 352, Height = 192 };
            Require(host.StagePreviewContent(body, 352, 192), "stage final-layout actual Host preview");
            source.UpdateLayout();
        }
        var bounds = presenter.AppliedPresentation.HostBounds;
        Window? background = null;
        EdgeCapsuleQueueProxyWindow? proxy = null;
        IDCompositionDesktopDevice? device = null;
        IDCompositionTarget? target = null;
        IDCompositionVisual2? visual = null;
        IUnknown? surface = null;
        EdgeCapsuleProxyNativeShape? shape = null;
        EdgeCapsuleProxySourceLease? lease = null;
        DesktopRoiCapture? capture = null;
        var phases = new List<object>();
        string? error = null;
        var tearingDown = false;
        var coverLost = false;
        try
        {
            background = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true,
                ResizeMode = ResizeMode.NoResize, Background = Brushes.Black, ShowActivated = false,
                ShowInTaskbar = false, Topmost = true, Width = bounds.Width / monitor.DpiScaleX,
                Height = bounds.Height / monitor.DpiScaleY };
            background.Show();
            Require(WindowNative.TrySetWindowDeviceBounds(background, bounds), "black backdrop bounds");
            source.Topmost = true;
            source.Hide(); source.Show();
            PixelPump(180);
            capture = new DesktopRoiCapture(bounds);
            Require(capture.Error == null, "capture initialization: " + capture.Error);
            var originalStart = Stopwatch.GetTimestamp();
            PixelPump(150);
            phases.Add(new { phase = "actual-source-baseline", start = originalStart, end = Stopwatch.GetTimestamp() });
            Require(host.TryAcquireProxySource(out lease), "actual Host independent live source");
            var iid = typeof(IDCompositionDesktopDevice).GUID;
            Marshal.ThrowExceptionForHR(OpacityDCompositionCreateDevice2(IntPtr.Zero, ref iid, out var ptr));
            device = new IDCompositionDesktopDevice(ptr);
            proxy = EdgeCapsuleQueueProxyWindow.TryCreate(bounds, true, _ => false, _ => { },
                () => coverLost = true, () => { }, () => { if (!tearingDown) coverLost = true; });
            Require(proxy != null, "native proxy output");
            device.CreateTargetForHwnd(proxy!.Handle, true, out target).CheckError();
            device.CreateVisual(out visual).CheckError();
            device.CreateSurfaceFromHwnd(nativeShape ? lease!.SourceHandle : host.Handle, out surface).CheckError();
            if (nativeShape)
            {
                shape = new EdgeCapsuleProxyNativeShape(device, visual, surface, lease!.Description);
                shape.SetStatic(presenter.AppliedPresentation, lease.Description);
            }
            else visual.SetContent(surface).CheckError();
            target.SetRoot(visual).CheckError();
            device.Commit().CheckError();
            proxy.Show(bounds, true);
            WindowNative.TryFlushDesktopComposition();
            Require(WindowNative.TrySetWindowCloaked(host.Handle, true), "cloak actual source");
            var proxyStart = Stopwatch.GetTimestamp();
            PixelPump(250);
            phases.Add(new { phase = "independent-source-baseline", start = proxyStart, end = Stopwatch.GetTimestamp() });
            SaveProxyAtlasDiagnostic(folder, lease!);

            if (resize) Require(presenter.Dispatch(EdgeCapsuleIntent.PreviewChanged(true)).Accepted, "open preview intent");
            else layout = layout with { ForcedContentOpacity = 0.4 };
            presenter.RequestPresentation(EdgeCapsuleMotion.Animate(EdgeCapsuleTransitionReason.State, 600));
            Reconcile(EdgeCapsuleDirty.Measure | EdgeCapsuleDirty.Presentation);
            WaitProxySourceLayout(lease!, presenter.AppliedPresentation);
            var transition = presenter.ActiveTransitionSnapshot ?? throw new Exception("alpha transition absent");
            Require(resize || (transition.Start.ContentOpacity == 1 && transition.Target.ContentOpacity == 0.4),
                "canonical actual Host alpha transition");
            shape?.Update(presenter.AppliedPresentation, transition, lease!.Description);
            device.Commit().CheckError();
            var before = applyCount;
            var submissionsBefore = shape?.SubmissionCount ?? 0;
            var start = Stopwatch.GetTimestamp();
            Thread.Sleep(resize ? 250 : 750); // Bounded test-only UI obstruction; capture has its own thread.
            phases.Add(new { phase = "ui-blocked-animation", start, end = Stopwatch.GetTimestamp(),
                applyCount = applyCount - before, nativeSubmissionDelta = (shape?.SubmissionCount ?? 0) - submissionsBefore,
                nativeShape, sourceHandle = nativeShape ? lease!.SourceHandle.ToInt64() : host.Handle.ToInt64(),
                from = transition.Start, target = transition.Target.ToFrame(),
                transitionStart = transition.StartedAtTimestamp, transitionDuration = transition.DurationTimestampTicks });
            Require(applyCount == before, "no WPF application while DComp animates");
            if (resize)
            {
                Reconcile(EdgeCapsuleDirty.Frame);
                Require(presenter.Dispatch(EdgeCapsuleIntent.PreviewChanged(false)).Accepted, "reverse preview intent");
                presenter.RequestPresentation(EdgeCapsuleMotion.Animate(EdgeCapsuleTransitionReason.Preview, 450));
                Reconcile(EdgeCapsuleDirty.Presentation);
                WaitProxySourceLayout(lease!, presenter.AppliedPresentation);
                shape?.Update(presenter.AppliedPresentation, presenter.ActiveTransitionSnapshot, lease!.Description);
                device.Commit().CheckError();
                before = applyCount;
                submissionsBefore = shape?.SubmissionCount ?? 0;
                var reverseStart = Stopwatch.GetTimestamp();
                Thread.Sleep(550);
                phases.Add(new { phase = "ui-blocked-reverse", start = reverseStart,
                    end = Stopwatch.GetTimestamp(), applyCount = applyCount - before,
                    nativeSubmissionDelta = (shape?.SubmissionCount ?? 0) - submissionsBefore, nativeShape,
                    from = presenter.ActiveTransitionSnapshot?.Start,
                    target = presenter.ActiveTransitionSnapshot?.Target.ToFrame() });
            }
            Reconcile(EdgeCapsuleDirty.Frame);
            // Closing can retire a source after the native animation has already reached its
            // compact endpoint. The old lease deliberately holds its last arranged tree to reveal.
            if (lease!.IsCurrent || lease.IsLayoutPending) WaitProxySourceLayout(lease, presenter.AppliedPresentation);
            shape?.Update(presenter.AppliedPresentation, presenter.ActiveTransitionSnapshot, lease.Description);
            device.Commit().CheckError();
            Require(host.ActualContentOpacity == (resize ? 1 : 0.4) &&
                presenter.AppliedPresentation.ContentOpacity == (resize ? 1 : 0.4),
                "actual source already owns its exact terminal alpha");
            PixelPump(180);
            var handoffStart = Stopwatch.GetTimestamp();
            // The actual HWND is already correct; leave the independent source and its effect
            // untouched until the ordinary authority swap completes. A channel delay must not
            // turn its alpha into a squared or unattenuated value.
            Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.Render);
            if (faultDelay > 0) PixelPump(faultDelay);
            device.Commit().CheckError();
            var swap = WindowNative.TrySetWindowCloakedBatchDetailed(
                new[] { new WindowNative.WindowCloakChange(host.Handle, false, true) },
                () => { target.SetRoot(null!).CheckError(); device.Commit().CheckError(); return true; },
                () => { target.SetRoot(visual).CheckError(); device.Commit().CheckError(); });
            Require(swap == WindowNative.WindowCloakBatchResult.Success, "coordinated source authority swap");
            phases.Add(new { phase = "handoff", start = handoffStart, end = Stopwatch.GetTimestamp(), faultDelay });
            PixelPump(240);
            Require(host.ActualContentOpacity == (resize ? 1 : 0.4),
                "real source terminal alpha restored");
            Require(!coverLost, "no unexpected output loss");
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            tearingDown = true;
            if (capture != null)
            {
                capture.Stop();
                using (var raw = File.Create(Path.Combine(folder, "roi-bgra.bin")))
                    foreach (var sample in capture.Samples) raw.Write(sample.Pixels);
                File.WriteAllText(Path.Combine(folder, "samples.json"), JsonSerializer.Serialize(
                    capture.Samples.Select((s, i) => new { index = i, s.AcquiredQpc, s.CopiedQpc,
                        s.LastPresentQpc, s.AccumulatedFrames, s.ProtectedContentMasked, s.Sha256,
                        byteOffset = (long)i * bounds.Width * bounds.Height * 4,
                        centerBlue = s.Pixels[((bounds.Height / 2) * bounds.Width + bounds.Width / 2) * 4] }),
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            File.WriteAllText(Path.Combine(folder, "results.json"), JsonSerializer.Serialize(new {
                name, resize, nativeShape, error, captureError = capture?.Error, bounds, qpcFrequency = Stopwatch.Frequency,
                samples = capture?.Samples.Count ?? 0, phases, applyCount,
                sourceAlpha = host.ActualContentOpacity, independentSource = lease?.SourceHandle.ToInt64(),
                scope = "Actual product WPF Host plus independent live atlas and native shape; " +
                    "controlled ROI, separate capture thread, not physical scanout or ordinary replay latency"
            }, new JsonSerializerOptions { WriteIndented = true }));
            capture?.Dispose();
            WindowNative.TrySetWindowCloaked(host.Handle, false);
            if (target != null && device != null) { target.SetRoot(null!); device.Commit(); }
            shape?.Dispose(); surface?.Dispose(); visual?.Dispose(); target?.Dispose(); device?.Dispose();
            proxy?.Dispose(); lease?.Dispose(); background?.Close(); presenter.ClearDeferredWork();
        }
        Require(error == null, error ?? "");
        Console.WriteLine("CAPTURE " + name + " " + folder);
    }

    private static void SaveProxyAtlasDiagnostic(string folder, EdgeCapsuleProxySourceLease lease)
    {
        // Diagnostic only, outside the measured blocked interval. This WPF render is for locating
        // plane/crop mistakes; only the separately captured desktop ROI is presented-pixel evidence.
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var source = typeof(EdgeCapsuleProxySourceLease).GetField("_source", flags)!.GetValue(lease)!;
        var canvas = (Canvas)source.GetType().GetField("_canvas", flags)!.GetValue(source)!;
        var d = lease.Description;
        var bitmap = new RenderTargetBitmap(d.NativeBounds.Width, d.NativeBounds.Height,
            d.Dpi.DpiScaleX * 96, d.Dpi.DpiScaleY * 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(folder, "atlas-wpf-diagnostic.png"))) encoder.Save(stream);
        var mappings = canvas.Children.OfType<System.Windows.Shapes.Rectangle>().Select(rect => new {
            rect.Width, rect.Height, Left = Canvas.GetLeft(rect), Top = Canvas.GetTop(rect),
            Viewbox = (rect.Fill as VisualBrush)?.Viewbox, Viewport = (rect.Fill as VisualBrush)?.Viewport,
            VisualSize = ((rect.Fill as VisualBrush)?.Visual as FrameworkElement)?.RenderSize
        });
        File.WriteAllText(Path.Combine(folder, "atlas-description.json"), JsonSerializer.Serialize(new {
            d.Generation, d.Revision, d.NativeBounds, d.Dpi, d.Compact, d.Preview, d.Close,
            d.ShellChrome, d.ShellOutline, d.LayoutFrame, mappings
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WaitProxySourceLayout(EdgeCapsuleProxySourceLease lease,
        EdgeCapsulePresentationFrame presentation)
    {
        if (lease.Synchronize(presentation)) return;
        Require(lease.IsLayoutPending, "source invalidated instead of awaiting natural layout");
        var loop = new DispatcherFrame();
        var ready = false;
        var timeout = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        void Updated()
        {
            ready = lease.Synchronize(presentation);
            if (ready || !lease.IsLayoutPending) loop.Continue = false;
        }
        void Invalidated() => loop.Continue = false;
        timeout.Tick += (_, _) => { timeout.Stop(); loop.Continue = false; };
        lease.Updated += Updated;
        lease.Invalidated += Invalidated;
        try { timeout.Start(); Dispatcher.PushFrame(loop); }
        finally { timeout.Stop(); lease.Updated -= Updated; lease.Invalidated -= Invalidated; }
        Require(ready, "natural WPF layout must update the retained proxy source before timeout");
    }

    private static void PixelPump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) {
            Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        try { timer.Start(); Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
    }

    [DllImport("dcomp.dll", EntryPoint = "DCompositionCreateDevice2")]
    private static extern int OpacityDCompositionCreateDevice2(IntPtr renderingDevice, ref Guid iid, out IntPtr device);
}
