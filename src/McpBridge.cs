using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    internal static bool TryDescribeInvalidToolArguments(
        CallToolRequestParams? request,
        out string message)
    {
        message = "";
        if (request == null ||
            !TryGetToolMethod(request.Name, out var method))
        {
            return false;
        }

        var parameters = method.GetParameters();
        foreach (var parameter in parameters)
        {
            if (parameter.ParameterType == typeof(CancellationToken) ||
                parameter.HasDefaultValue)
            {
                continue;
            }

            if (request.Arguments == null ||
                !request.Arguments.ContainsKey(parameter.Name!))
            {
                message =
                    $"PaperTodo error (invalid_params): {parameter.Name} is required.";
                return true;
            }
        }

        if (request.Arguments == null)
        {
            return false;
        }

        foreach (var argument in request.Arguments)
        {
            var parameter = parameters.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.Name,
                    argument.Key,
                    StringComparison.Ordinal));
            if (parameter == null ||
                parameter.ParameterType == typeof(CancellationToken))
            {
                continue;
            }

            try
            {
                _ = JsonSerializer.Deserialize(
                    argument.Value.GetRawText(),
                    parameter.ParameterType,
                    ToolArgumentJsonOptions);
            }
            catch (JsonException ex)
            {
                message = FormatInvalidToolArgument(
                    argument.Key,
                    parameter.ParameterType,
                    ex);
                return true;
            }
            catch (NotSupportedException)
            {
                // The SDK owns special parameter binding. If a parameter cannot
                // be reproduced safely here, preserve its original error path.
            }
        }

        return false;
    }

    private static bool TryGetToolMethod(
        string toolName,
        out MethodInfo method)
    {
        foreach (var candidate in typeof(McpTools).GetMethods(
                     BindingFlags.Instance | BindingFlags.Public))
        {
            var attribute =
                candidate.GetCustomAttribute<McpServerToolAttribute>();
            if (attribute?.Name != null &&
                string.Equals(
                    attribute.Name,
                    toolName,
                    StringComparison.Ordinal))
            {
                method = candidate;
                return true;
            }
        }

        method = null!;
        return false;
    }

    private static string FormatInvalidToolArgument(
        string parameterName,
        Type parameterType,
        JsonException exception)
    {
        var path = parameterName;
        if (!string.IsNullOrWhiteSpace(exception.Path) &&
            !string.Equals(exception.Path, "$", StringComparison.Ordinal))
        {
            path += exception.Path!.StartsWith("$", StringComparison.Ordinal)
                ? exception.Path[1..]
                : "." + exception.Path;
        }

        const string missingMarker =
            "was missing required properties, including the following:";
        var missingIndex = exception.Message.IndexOf(
            missingMarker,
            StringComparison.Ordinal);
        if (missingIndex >= 0)
        {
            var fields = exception.Message[
                (missingIndex + missingMarker.Length)..];
            var diagnosticsIndex = fields.IndexOf(
                " Path:",
                StringComparison.Ordinal);
            if (diagnosticsIndex < 0)
            {
                diagnosticsIndex = fields.IndexOf(
                    " |",
                    StringComparison.Ordinal);
            }
            if (diagnosticsIndex >= 0)
            {
                fields = fields[..diagnosticsIndex];
            }

            fields = fields.Trim().TrimEnd('.');
            if (fields.Length > 0)
            {
                fields = NormalizeMissingFieldNames(
                    parameterType,
                    fields);
                return
                    $"PaperTodo error (invalid_params): {path} is missing required field(s): {fields}.";
            }
        }

        return
            $"PaperTodo error (invalid_params): {path} has the wrong type or shape.";
    }

    private static string NormalizeMissingFieldNames(
        Type parameterType,
        string fields)
    {
        var contractType = parameterType;
        if (contractType.IsArray)
        {
            contractType = contractType.GetElementType() ?? contractType;
        }
        else if (contractType.IsGenericType &&
                 contractType.GetGenericArguments() is [var itemType])
        {
            contractType = itemType;
        }

        var names = fields.Split(
            ',',
            StringSplitOptions.TrimEntries |
            StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < names.Length; index++)
        {
            var property = contractType.GetProperty(
                names[index],
                BindingFlags.Instance | BindingFlags.Public);
            names[index] =
                property?.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                ?? names[index];
        }

        return string.Join(", ", names);
    }
}
