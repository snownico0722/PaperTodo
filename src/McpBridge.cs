using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PaperTodo;

internal static class McpBridge
{
    public const string CommandLineSwitch = "--mcp";
    private static readonly JsonSerializerOptions ToolArgumentJsonOptions =
        new(JsonSerializerDefaults.Web);

    public static bool IsRequested(IReadOnlyList<string> args)
        => args.Any(argument =>
            string.Equals(
                argument?.Trim(),
                CommandLineSwitch,
                StringComparison.OrdinalIgnoreCase));

    public static async Task RunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        var builder = Host.CreateEmptyApplicationBuilder(
            new HostApplicationBuilderSettings
            {
                Args = args,
                ApplicationName = typeof(McpBridge).Assembly.FullName
            });

        // MCP owns stdout. Diagnostics must never corrupt the stdio protocol.
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<McpPipeClient>();
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithRequestFilters(filters =>
                filters.AddCallToolFilter(TranslateToolArgumentErrors))
            .WithTools<McpTools>();

        await builder.Build().RunAsync(cancellationToken);
    }

    internal static McpRequestHandler<CallToolRequestParams, CallToolResult>
        TranslateToolArgumentErrors(
            McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, cancellationToken) =>
        {
            try
            {
                return await next(request, cancellationToken);
            }
            catch (Exception ex)
                when (ex is JsonException or ArgumentException &&
                      TryDescribeInvalidToolArguments(
                          request.Params,
                          out var message))
            {
                throw new McpException(message, ex);
            }
        };

    // Diagnose only failed SDK bindings; let the JSON serializer validate the
    // nested tool shape instead of maintaining a second recursive schema walker.
    internal static bool TryDescribeInvalidToolArguments(
        CallToolRequestParams? request,
        out string message)
    {
        message = "";
        if (request == null)
        {
            return false;
        }

        var method = typeof(McpTools)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(candidate =>
                candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name ==
                request.Name);
        if (method == null)
        {
            return false;
        }

        foreach (var parameter in method.GetParameters())
        {
            if (parameter.ParameterType == typeof(CancellationToken))
            {
                continue;
            }

            if (request.Arguments == null ||
                !request.Arguments.TryGetValue(parameter.Name!, out var value))
            {
                if (parameter.HasDefaultValue)
                {
                    continue;
                }

                message =
                    $"PaperTodo error (invalid_params): {parameter.Name} is required.";
                return true;
            }

            try
            {
                _ = JsonSerializer.Deserialize(
                    value.GetRawText(),
                    parameter.ParameterType,
                    ToolArgumentJsonOptions);
            }
            catch (JsonException ex)
            {
                var path = parameter.Name!;
                if (ex.Path is { Length: > 1 } nestedPath)
                {
                    path += nestedPath.StartsWith("$", StringComparison.Ordinal)
                        ? nestedPath[1..]
                        : "." + nestedPath;
                }

                message =
                    $"PaperTodo error (invalid_params): {path}: {ex.Message}";
                return true;
            }
        }

        return false;
    }
}
