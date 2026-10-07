from pathlib import Path

p = Path('tests/PaperTodo.MicaChecks/Program.cs')
s = p.read_text(encoding='utf-8-sig')
needle = '        var temp = Path.Combine(Path.GetTempPath(), "PaperTodo.NativeMicaChecks", Guid.NewGuid().ToString("N"));'
assert s.count(needle) == 1
s = s.replace(needle, '''        using (var boundaryController = new AppController())
        {
            try
            {
                if (Environment.GetEnvironmentVariable("BOUNDARY_AREA") == "environment")
                    MaterialEnvironmentBoundaryChecks.Run(boundaryController);
                else MenuOpeningBoundaryChecks.Run(boundaryController);
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
''' + needle)
p.write_text(s, encoding='utf-8')
p = Path('tests/PaperTodo.LifecycleChecks/Program.cs')
s = p.read_text(encoding='utf-8-sig')
needle = '                await MasterMaterialHandoffChecks.Run(controller, windows);'
assert s.count(needle) == 1
s = s.replace(needle, '                await QueueFeatureBoundaryChecks.Run(controller, windows, Environment.GetEnvironmentVariable("PAPER_BOUNDARY_CASE")!);')
p.write_text(s, encoding='utf-8')

p = Path('tests/PaperTodo.LifecycleChecks/QueueFeatureBoundaryChecks.cs')
s = p.read_text(encoding='utf-8-sig')
needle = '                MouseEvent(2, 0, 0, 0, UIntPtr.Zero); MouseEvent(4, 0, 0, 0, UIntPtr.Zero);'
assert s.count(needle) == 1
s = s.replace(needle, '''                var publication = Stopwatch.StartNew();
                // Geometry/apply completion is not proof that a freshly reshown layered HWND has
                // presented input pixels to DWM. Do not send the probe click into the desktop.
                await Until(() => WindowFromPoint(point) == restored.Handle,
                    mode + ": restored real capsule is the actual native hit target");
                Console.WriteLine($"NATIVE_READY mode={mode} elapsedMs={publication.Elapsed.TotalMilliseconds:F2}");
''' + needle)
p.write_text(s, encoding='utf-8')
