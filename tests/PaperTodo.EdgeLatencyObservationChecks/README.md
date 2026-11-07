# Edge latency observation behavior checks

Run on Windows x64 with the .NET 10 desktop runtime:

```powershell
dotnet run --project tests/PaperTodo.EdgeLatencyObservationChecks -c Debug -- --output <artifact-directory>
```

This independent executable links the production Debug observers and `EdgeDiagnosticJournal`. Its minimal event facade forwards observations into the real bounded journal. It creates only its own hidden `HwndSource`, then destroys it; it does not start PaperTodo, restore papers, replay input, subscribe Rendering, or install a timer.

The retained checks protect the host while probes are enabled: native messages retain their full-width arguments and results and are forwarded exactly once, including reentrancy, removal/reinstallation and HWND destruction. Dispatcher priority, FIFO, inline execution and cancellation are compared with observation disabled, enabled and removed. Thread-message hooks must preserve the original message and handled flag. The journal is sealed to the requested directory when the check exits.

The former numeric-reader type matrix, diagnostic correlation-field golden checks and live DWM clock calibration have retired from permanent regression coverage. Those detailed measurements belong to the diagnostic investigation that needs them; this suite does not claim to validate every captured metric or a physical frame rate.

Release builds return an explicit nonzero message because the production observers only exist in Debug. This is a behavior check for the probes, not an application performance benchmark.
