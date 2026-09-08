# Threading regression checks

Run on Windows with .NET 10:

```powershell
dotnet run --project .\tests\PaperTodo.ThreadingChecks\PaperTodo.ThreadingChecks.csproj -c Release
```

Each case runs in a fresh process, with a real STA WPF Application, Dispatcher and templates. Worker-first cases explicitly initialize the production type on a worker before the UI applies its resources. Runtime tests hold queued calls until after lease invalidation, and check that cache/model publication follows UI execution order. Pipe tests use unique named pipes and keep an incomplete peer connected while checking timeout recovery and shutdown cancellation.

The Runtime fixture bypasses AppController startup and supplies only in-memory state to the actual production API/controller methods. These checks never open the user's data stores or start plugins. They do not replace full application startup, tray interaction, multi-monitor/DPI or native/Web plugin end-to-end manual tests.
