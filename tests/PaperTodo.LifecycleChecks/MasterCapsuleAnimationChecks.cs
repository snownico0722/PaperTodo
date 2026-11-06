using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PaperTodo;

internal static class MasterCapsuleAnimationChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Gif = "R0lGODlhCAAIAIEAAAAAAP8AAAAAAAAAACH/C05FVFNDQVBFMi4wAwEBAAAh+QQFBQAAACwAAAAACAAIAAAIFAABCBw4MEAAggINIlzIsKHDhwEBACH5BAkNAAAALAQAAQACAAIAgQAAAP8AAAD/AAAAAAgGAAUIFBAQACH5BA0HAAAALAEAAQACAAUAgQAAAP8AAAAA/wAAAAgMAAMIDAAAgICDAgICACH5BAUUAAAALAEABAAFAAIAgQAAAP8AAP//AAAAAAgLAAEIFCBAIACCAQEAOw==";
    private const string WebpRed = "UklGRhwAAABXRUJQVlA4TA8AAAAvB8ABAAcQ/Y/+ByKi/wEA";
    private const string WebpGreen = "UklGRhwAAABXRUJQVlA4TA8AAAAvB8ABAAfQ/4j+ByKi/wEA";
    private static readonly string[] GifExpected = ["AAD//wAA//8AAAAAAAAAAAAAAAAAAP//AAD//wAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==", "AAD//wAA//8AAAAAAP8A/wD/AP8AAP//AAD//wAAAAAA/wD/AP8A/wAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==", "AAD//wAA//8AAAAAAAAAAAAAAAAAAP//AAD//wAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/wAA//8AAP8AAAAAAAAAAAAAAAD/AAD//wAA/wAAAAAAAAAAAAAAAA==", "AAD//wAA//8AAAAAAAAAAAAAAAAAAP//AAD//wAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAP///wD///8AAAAAAAAAAAAAAAAA////AP///w=="];

    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static object? Call(object owner, string name, params object?[] args) =>
        owner.GetType().GetMethod(name, Private)!.Invoke(owner, args);

    internal static void Prepare(bool dragOnly)
    {
        if (!dragOnly) VerifyLoading();
        var directory = AppContext.BaseDirectory;
        if (!dragOnly)
        {
            WritePng(directory, "papertodomain1.png", 8, 8, 35);
            WritePng(directory, "papertodomain2.png", 8, 8, 85);
            WritePng(directory, "papertodomain10.png", 8, 8, 135);
        }
        File.WriteAllBytes(Path.Combine(directory, "papertodomaindrag.gif"), Convert.FromBase64String(Gif));
    }

    private static void VerifyLoading()
    {
        var root = Path.Combine(Path.GetTempPath(), "PaperTodo.AnimationAssets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string Case(string name)
            {
                var path = Path.Combine(root, name);
                Directory.CreateDirectory(path);
                return path;
            }
            var missing = MasterCapsuleSkin.LoadVisualsFromDirectory(Case("missing"));
            Require(missing.Normal == null && missing.DragOrNormal == null, "Absent artwork must preserve the default pill");
            VerifyDisplayRaster(Case("display-size"));

            var priority = Case("priority");
            WritePng(priority, "papertodomain.png", 8, 8, 250);
            WritePng(priority, "papertodomain10.png", 8, 8, 110);
            WritePng(priority, "papertodomain2.png", 8, 8, 60, dpiX: 300, dpiY: 150);
            WritePng(priority, "papertodomain1.png", 8, 8, 10);
            WritePng(priority, "papertodomaindrag1.png", 8, 8, 240);
            var sequence = MasterCapsuleSkin.LoadClip(priority, "papertodomain")!;
            Require(sequence.FrameCount == 3 && Red(sequence.FrameAt(0)) == 10 &&
                    Red(sequence.FrameAt(1)) == 60 && Red(sequence.FrameAt(2)) == 110,
                "Numbered frames must precede still artwork and sort numerically");
            // Printing DPI must not stretch one pose relative to the others.
            for (var i = 0; i < sequence.FrameCount; i++)
                Require(sequence.FrameAt(i).Width == 8 && sequence.FrameAt(i).Height == 8,
                    "Source DPI changed the common animation canvas");
            File.WriteAllText(Path.Combine(priority, "papertodomain.gif"), "broken");
            Require(MasterCapsuleSkin.LoadClip(priority, "papertodomain")!.FrameCount == 3,
                "A broken GIF shadowed a valid sequence");
            File.WriteAllBytes(Path.Combine(priority, "papertodomain.gif"), Convert.FromBase64String(Gif));
            var gif = MasterCapsuleSkin.LoadClip(priority, "papertodomain")!;
            Require(gif.FrameCount == 4 && gif.FirstFrame.PixelWidth == 5 && gif.FirstFrame.PixelHeight == 5,
                "GIF must take priority and use the whole animation's transparent union");
            for (var i = 0; i < gif.FrameCount; i++)
                Require(Pixels(gif.FrameAt(i)).SequenceEqual(Convert.FromBase64String(GifExpected[i])),
                    "GIF offset/transparency/disposal composition is wrong at frame " + i);

            var fallback = Case("fallback");
            WritePng(fallback, "papertodomain.png", 8, 8, 90);
            File.WriteAllText(Path.Combine(fallback, "papertodomain1.png"), "broken");
            var visuals = MasterCapsuleSkin.LoadVisualsFromDirectory(fallback);
            Require(visuals.Normal?.FrameCount == 1 && Red(visuals.Normal.FirstFrame) == 90 &&
                    visuals.DragOrNormal?.FrameCount == 1 && Red(visuals.DragOrNormal.FirstFrame) == 90,
                "Malformed sequences must fall back to still artwork; absent drag must show normal artwork");
            WritePng(fallback, "papertodomain1.png", 8, 8, 40);
            WritePng(fallback, "papertodomain2.png", 9, 8, 50);
            Require(Red(MasterCapsuleSkin.LoadClip(fallback, "papertodomain")!.FirstFrame) == 90,
                "Mismatched sequence canvases must not be silently stretched or partially loaded");

            var union = Case("union");
            WritePng(union, "papertodomain01.png", 10, 8, 10, new Int32Rect(2, 2, 2, 1));
            WritePng(union, "papertodomain02.png", 10, 8, 20, new Int32Rect(0, 0, 0, 0));
            WritePng(union, "papertodomain09.png", 10, 8, 30, new Int32Rect(7, 5, 2, 1));
            var clip = MasterCapsuleSkin.LoadClip(union, "papertodomain")!;
            Require(clip.FrameCount == 3, "Missing indices must not truncate a sequence");
            for (var i = 0; i < clip.FrameCount; i++)
                Require(clip.FrameAt(i) is { PixelWidth: 7, PixelHeight: 4, IsFrozen: true },
                    "Every frame must share a frozen common crop, including an empty frame");
            Require(Pixels(clip.FrameAt(1)).All(value => value == 0), "Empty animation frames must remain empty");
            var last = Pixels(clip.FrameAt(2));
            Require(last[(3 * 7 + 5) * 4 + 3] == 255 && last[3] == 0,
                "Independent frame trimming moved a pose relative to the shared canvas");

            var webp = Case("webp");
            File.WriteAllBytes(Path.Combine(webp, "papertodomain1.webp"), Convert.FromBase64String(WebpRed));
            File.WriteAllBytes(Path.Combine(webp, "papertodomain2.webp"), Convert.FromBase64String(WebpGreen));
            var webpClip = MasterCapsuleSkin.LoadClip(webp, "papertodomain");
            WritePng(webp, "papertodomain.png", 8, 8, 80);
            if (webpClip == null)
            {
                Require(Red(MasterCapsuleSkin.LoadClip(webp, "papertodomain")!.FirstFrame) == 80,
                    "An unavailable WebP codec must fall back to the static PNG");
                Console.WriteLine("SKIP WebP sequence decoding: codec unavailable; static PNG fallback verified");
            }
            else
            {
                Require(webpClip.FrameCount == 2 && Red(webpClip.FirstFrame) == 255 &&
                        Pixels(webpClip.FrameAt(1))[1] == 255,
                    "WebP sequence content or order is wrong");
                Console.WriteLine("WEBP_DECODE_PASS");
            }
            Console.WriteLine("PASS artwork loading, GIF disposal, fallback, numeric ordering and common crop");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void VerifyDisplayRaster(string directory)
    {
        WritePng(directory, "papertodomain1.png", 1000, 1000, 30);
        WritePng(directory, "papertodomain2.png", 1000, 1000, 170);
        var clip = MasterCapsuleSkin.LoadClip(directory, "papertodomain")!;
        var logicalWidth = MasterCapsuleSkin.DisplayWidthDip(clip, 50);
        var original = Pixels(clip.FirstFrame);
        var normal = new object();
        var highDpi = new object();
        clip.RequestDisplaySize(normal, new Size(200, 200));
        clip.ApplyDisplaySize();
        Require(clip.CanvasWidth == 1000 && clip.CanvasHeight == 1000 && clip.FrameCount == 2 &&
                clip.FirstFrame is { PixelWidth: 200, PixelHeight: 200, IsFrozen: true } &&
                clip.FrameAt(1) is { PixelWidth: 200, PixelHeight: 200, IsFrozen: true },
            "200-pixel display must retain 200-pixel frames without changing source geometry or frame count");
        Require(Red(clip.FirstFrame) == 30 && Red(clip.FrameAt(1)) == 170,
            "Reusing the decoder scratch buffer corrupted previously created frames");
        clip.RequestDisplaySize(highDpi, new Size(400, 400));
        clip.ApplyDisplaySize();
        Require(clip.FirstFrame.PixelWidth == 400 && MasterCapsuleSkin.DisplayWidthDip(clip, 50) == logicalWidth,
            "The largest visible DPI demand must win without changing capsule geometry");
        clip.RequestDisplaySize(highDpi, null);
        clip.ApplyDisplaySize();
        Require(clip.FirstFrame.PixelWidth == 200, "Closing the high-DPI view did not release its larger raster");
        clip.RequestDisplaySize(normal, new Size(2000, 2000));
        clip.ApplyDisplaySize();
        Require(clip.FirstFrame.PixelWidth == 1000 && Pixels(clip.FirstFrame).SequenceEqual(original),
            "Enlarging must reload original detail and must never manufacture larger cached pixels");
        clip.RequestDisplaySize(normal, new Size(200, 200));
        clip.ApplyDisplaySize();
        var retained = Pixels(clip.FirstFrame);
        File.Delete(Path.Combine(directory, "papertodomain1.png"));
        clip.RequestDisplaySize(normal, new Size(800, 800));
        clip.ApplyDisplaySize();
        Require(clip.FirstFrame is { PixelWidth: 200, PixelHeight: 200 } &&
                Pixels(clip.FirstFrame).SequenceEqual(retained) && Red(clip.FrameAt(1)) == 170,
            "A missing source discarded or corrupted the playable frames");
        clip.RequestDisplaySize(normal, null);
        Console.WriteLine("PASS display raster: 1000→200→400→200→1000, shared DPI demand and source removal");
    }

    internal static async Task Verify(AppController controller)
    {
        var master = controller.MasterCapsuleForQueue("", EdgeCapsuleEdge.Right)!;
        Require(master != null && master.IsVisible, "A visible master is required");
        var player = Field<MasterCapsuleAnimationPlayer>(master, "_skinPlayer");
        var image = Field<Image>(master, "_skinImage");
        var normal = Field<MasterCapsulePlayback?>(master, "_normalSkinPlayback");
        controller.PublicSettings.Set("capsule.master_animation_fps", JsonSerializer.SerializeToElement(30));
        Require(controller.State.MasterCapsuleAnimationFps == 30, "Animation speed was not committed");

        Require(normal is { Clip.FrameCount: 3, FramesPerSecond: 30 } && player.IsRunning,
            "Normal numbered frames did not start or react to the speed setting");
        var before = player.PresentedFrameChanges;
        await RequireAdvancing(player, before, "normal");
        Require(WindowNative.TryGetWindowDeviceBounds(master, out var initialBounds), "Master bounds unavailable");
        var drag = (MasterCapsulePlayback)Call(master, "GetMasterDragPlayback")!;
        Call(master, "ApplyMasterSkinPlayback", drag);
        Require(player.Playback?.Clip.FrameCount == 4 && image.Source != null && player.IsRunning,
            "The drag gesture did not select the independent GIF playhead");
        var count = player.PresentedFrameChanges;
        await RequireAdvancing(player, count, "drag");
        Require(WindowNative.TryGetWindowDeviceBounds(master, out var playingBounds) && playingBounds.Equals(initialBounds),
            "Playing a drag pose must not resize or move the master HWND");
        Call(master, "RestoreMasterSkinPlayback");
        Require(player.Playback?.Clip.FrameCount == 3 && image.Source != null && player.IsRunning,
            "Gesture completion did not restore the normal artwork");

        master.Hide();
        var hiddenCount = player.PresentedFrameChanges;
        Require(!player.IsRunning, "A hidden master retained its animation timer");
        await Task.Delay(90);
        Require(player.PresentedFrameChanges == hiddenCount, "A hidden master kept advancing its Image");
        master.Show();
        await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render);

        Require(controller.TryCreateMasterQueueFloatingDragHostOptions("", EdgeCapsuleEdge.Right, "▾", "5", out var options),
            "Floating master options unavailable");
        var animatedOptions = options with
        {
            MasterSkinPlayback = drag,
            MasterSkinEdge = EdgeCapsuleEdge.Right
        };
        Require(WindowNative.TryGetCursorScreenPosition(out var pointer), "Cursor position unavailable");
        var floating = EdgeCapsuleDragWindow.Rent(animatedOptions);
        MasterCapsuleAnimationPlayer floatingPlayer;
        try
        {
            floating.ShowWithEntrance(pointer, false, 1, 0);
            floatingPlayer = Field<MasterCapsuleAnimationPlayer>(floating, "_masterSkinPlayer");
            Require(floatingPlayer.Playback?.Clip.FrameCount == 4 && floatingPlayer.IsRunning,
                "Detached dragging must play the independent drag artwork");
            await VerifyNativeMoveLoop(floating, floatingPlayer);
            floating.SetMasterSkinPlayback(normal, EdgeCapsuleEdge.Left);
            Require(floatingPlayer.Playback?.Clip.FrameCount == 3 && floatingPlayer.IsRunning,
                "Return-flight cover did not restore the destination artwork");
        }
        finally { floating.ReturnToPool(); }
        floatingPlayer = Field<MasterCapsuleAnimationPlayer>(floating, "_masterSkinPlayer");
        Require(!floatingPlayer.IsRunning && floatingPlayer.Playback == null &&
                Field<Image>(floating, "_masterSkinImage").Source == null,
            "Returning a pooled host must stop playback and release the previous visual binding");

        // Equality-cached options must still restore playback after ReturnToPool cleared it.
        var same = EdgeCapsuleDragWindow.Rent(animatedOptions);
        try
        {
            same.ShowWithEntrance(pointer, false, 1, 0);
            var samePlayer = Field<MasterCapsuleAnimationPlayer>(same, "_masterSkinPlayer");
            Require(samePlayer.Playback?.Clip.FrameCount == 4 && samePlayer.IsRunning,
                "An identical options lease failed to resume the animation");
        }
        finally { same.ReturnToPool(); }
        var ordinary = EdgeCapsuleDragWindow.Rent(options);
        try
        {
            Require(!ordinary.HasMasterSkin && !Field<MasterCapsuleAnimationPlayer>(ordinary, "_masterSkinPlayer").IsRunning &&
                    Field<Grid>(ordinary, "_shell").Visibility == Visibility.Visible,
                "An ordinary capsule inherited the preceding master animation or hidden shell");
        }
        finally { ordinary.ReturnToPool(); }
        Console.WriteLine("PASS normal/drag playback, live speed change, fixed geometry, hidden pause, handoff and lease cleanup");
    }

    private static async Task RequireAdvancing(MasterCapsuleAnimationPlayer player, long before, string state)
    {
        // Startup prewarming may occupy the Dispatcher before its first low-priority WM_TIMER.
        // Wait for observed progress with a deadline, not a fixed 150ms scheduling assumption.
        var elapsed = Stopwatch.StartNew();
        while (player.PresentedFrameChanges <= before + 1 && elapsed.ElapsedMilliseconds < 3000)
            await Task.Delay(40);
        Console.WriteLine($"ANIMATION_PROGRESS state={state} elapsed={elapsed.ElapsedMilliseconds} changes={player.PresentedFrameChanges - before} running={player.IsRunning}");
        Require(player.IsRunning && player.PresentedFrameChanges > before + 1,
            $"Visible {state} animation did not advance before the deadline");
    }

    private static async Task VerifyNativeMoveLoop(EdgeCapsuleDragWindow host, MasterCapsuleAnimationPlayer player)
    {
        var hwnd = new WindowInteropHelper(host).Handle;
        var source = HwndSource.FromHwnd(hwnd)!;
        var releaseTimer = (nuint)0x634C105;
        var insideMove = false;
        var renderedChanges = 0;
        ImageSource? lastRendered = null;
        var image = Field<Image>(host, "_masterSkinImage");
        EventHandler rendered = (_, _) =>
        {
            if (!insideMove || ReferenceEquals(lastRendered, image.Source)) return;
            lastRendered = image.Source;
            renderedChanges++;
        };
        HwndSourceHook release = (IntPtr h, int message, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (message == 0x0113 && (nuint)w == releaseTimer)
            {
                KillTimer(h, releaseTimer);
                MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
                handled = true;
            }
            return IntPtr.Zero;
        };
        source.AddHook(release);
        CompositionTarget.Rendering += rendered;
        // Test-only fail-safe releases synthetic input even if the modal loop stops dispatching.
        using var watchdog = new System.Threading.Timer(_ => MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero), null, 2500, Timeout.Infinite);
        try
        {
            MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
            // GetKeyState reflects this thread's dispatched input, not merely injected hardware
            // state. Calling SendMessage before the down message is retrieved ends move at once.
            var inputWait = Stopwatch.StartNew();
            while ((GetKeyState(1) & 0x8000) == 0 && inputWait.ElapsedMilliseconds < 1000)
                await Task.Delay(10);
            Require((GetKeyState(1) & 0x8000) != 0, "Injected drag press was not dispatched");
            Require(SetTimer(hwnd, releaseTimer, 500, IntPtr.Zero) != 0, "Native test release timer failed");
            var before = player.PresentedFrameChanges;
            var watch = Stopwatch.StartNew();
            insideMove = true;
            var result = host.RunNativeDragFromCursor();
            insideMove = false;
            Console.WriteLine($"NATIVE_DRAG_PROBE outcome={result.Result} elapsed={watch.ElapsedMilliseconds} changes={player.PresentedFrameChanges - before} rendered={renderedChanges}");
            Require(result.Result == EdgeCapsuleNativeDragResult.Completed && watch.ElapsedMilliseconds >= 200,
                "The native caption-move probe did not enter a sustained drag");
            Require(player.PresentedFrameChanges > before + 1 && renderedChanges >= 2,
                "Animation frames did not both advance and render inside the native caption-move loop");
            Console.WriteLine($"NATIVE_DRAG_ANIMATION_PASS changes={player.PresentedFrameChanges - before} rendered={renderedChanges}");
        }
        finally
        {
            insideMove = false;
            KillTimer(hwnd, releaseTimer);
            MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
            CompositionTarget.Rendering -= rendered;
            source.RemoveHook(release);
        }
    }

    private static byte Red(BitmapSource source) => Pixels(source)[2];
    private static byte[] Pixels(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[source.PixelWidth * source.PixelHeight * 4];
        converted.CopyPixels(bytes, source.PixelWidth * 4, 0);
        return bytes;
    }
    private static void WritePng(string directory, string name, int width, int height, byte red,
        Int32Rect? region = null, double dpiX = 96, double dpiY = 96)
    {
        var pixels = new byte[width * height * 4];
        var bounds = region ?? new Int32Rect(0, 0, width, height);
        for (var y = bounds.Y; y < bounds.Y + bounds.Height; y++)
        for (var x = bounds.X; x < bounds.X + bounds.Width; x++)
        {
            var offset = (y * width + x) * 4;
            pixels[offset + 2] = red;
            pixels[offset + 3] = 255;
        }
        var bitmap = BitmapSource.Create(width, height, dpiX, dpiY, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, name));
        encoder.Save(output);
    }

    [DllImport("user32.dll")]
    private static extern nuint SetTimer(IntPtr hwnd, nuint id, uint interval, IntPtr callback);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool KillTimer(IntPtr hwnd, nuint id);
    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);
    [DllImport("user32.dll", EntryPoint = "mouse_event")]
    private static extern void MouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
}
