namespace PaperTodo;

public partial class App
{
    public App()
    {
        // MCP is a stdio bridge process and never owns GUI edge surfaces or usage telemetry.
        if (McpBridge.IsRequested(Environment.GetCommandLineArgs()))
        {
            return;
        }

        InitializeTelemetry();

        // The controller's prewarm coordinator schedules graphics resources together with real
        // queue readiness, after the first preview pass and startup shells settle. Construction
        // itself never preclaims HWNDs or competes with the preview-first startup path.
    }
}
