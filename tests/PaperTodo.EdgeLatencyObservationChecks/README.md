# Edge latency observation behavior checks

Run on Windows x64 with the .NET 10 desktop runtime:

```powershell
dotnet run --project tests/PaperTodo.EdgeLatencyObservationChecks -c Debug -- --output <artifact-directory>
```

This independent executable links the two production Debug observers and `EdgeDiagnosticJournal`. Its minimal event facade forwards observations into the real bounded journal. It creates only its own hidden `HwndSource`, then destroys it; it does not start PaperTodo, restore papers, replay input, subscribe Rendering, or install a timer.

The checks exercise native message forwarding exactly once with full-width arguments and results, reentrant messages and nested batch restoration, scope exit, Remove/reinstall, and HWND destruction. Dispatcher checks compare known priority/FIFO/inline execution with and without observation, verify operation lifecycle and cancellation records, and check Remove/reinstall. They do not assert machine timings, WPF private field names, physical frame rates, or absence of measurement overhead. The journal is sealed to the requested directory when the check exits.

Release builds return an explicit nonzero message because the production observers only exist in Debug. This is a behavior check for the probes, not an application performance benchmark.
