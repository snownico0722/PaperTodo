using System.Reflection;
using System.Runtime.CompilerServices;
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

            if (TryDescribeMissingRequiredMember(
                    argument.Value,
                    parameter.ParameterType,
                    argument.Key,
                    out message))
            {
                return true;
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

    private static bool TryDescribeMissingRequiredMember(
        JsonElement value,
        Type expectedType,
        string path,
        out string message)
    {
        message = "";
        expectedType = Nullable.GetUnderlyingType(expectedType) ?? expectedType;
        if (value.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        if (TryGetCollectionElementType(expectedType, out var itemType))
        {
            if (value.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                if (TryDescribeMissingRequiredMember(
                        item,
                        itemType,
                        $"{path}[{index}]",
                        out message))
                {
                    return true;
                }
                index++;
            }
            return false;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in expectedType.GetProperties(
                     BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.IsDefined(
                    typeof(RequiredMemberAttribute),
                    inherit: true) &&
                !property.IsDefined(
                    typeof(JsonRequiredAttribute),
                    inherit: true))
            {
                continue;
            }

            var jsonName =
                property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                ?? ToolArgumentJsonOptions.PropertyNamingPolicy?.ConvertName(
                    property.Name)
                ?? property.Name;
            if (!value.TryGetProperty(jsonName, out var child))
            {
                message =
                    $"PaperTodo error (invalid_params): {path}.{jsonName} is required.";
                return true;
            }

            if (TryDescribeMissingRequiredMember(
                    child,
                    property.PropertyType,
                    $"{path}.{jsonName}",
                    out message))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetCollectionElementType(
        Type type,
        out Type elementType)
    {
        if (type.IsArray)
        {
            elementType = type.GetElementType()!;
            return true;
        }

        var enumerable = type
            .GetInterfaces()
            .Append(type)
            .FirstOrDefault(candidate =>
                candidate.IsGenericType &&
                candidate.GetGenericTypeDefinition() ==
                    typeof(IEnumerable<>));
        if (enumerable != null)
        {
            elementType = enumerable.GetGenericArguments()[0];
            return true;
        }

        elementType = null!;
        return false;
    }

    private static string FormatInvalidToolArgument(
        string parameterName,
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

        return
            $"PaperTodo error (invalid_params): {path} has the wrong type or shape.";
    }

}
