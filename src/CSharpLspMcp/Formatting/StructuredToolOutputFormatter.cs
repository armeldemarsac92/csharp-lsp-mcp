using System.Text.Json;
using System.Text.Json.Serialization;
using CSharpLspMcp.Contracts.Common;

namespace CSharpLspMcp.Formatting;

public static class StructuredToolOutputFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static string FormatSuccess<TResult>(string toolName, string? format, TResult result)
        where TResult : IStructuredToolResult
    {
        if (ShouldRenderSummary(format))
            return result.Summary;

        return JsonSerializer.Serialize(
            new StructuredToolEnvelope<TResult>(
                SchemaVersion: 1,
                Tool: toolName,
                Success: true,
                Summary: result.Summary,
                Data: result,
                Error: null),
            JsonOptions);
    }

    public static string FormatError(string toolName, string? format, string code, string message)
    {
        if (ShouldRenderSummary(format))
            return $"Error: {message}";

        return JsonSerializer.Serialize(
            new StructuredToolEnvelope<object>(
                SchemaVersion: 1,
                Tool: toolName,
                Success: false,
                Summary: message,
                Data: null,
                Error: new StructuredToolError(code, message)),
            JsonOptions);
    }

    private static bool ShouldRenderSummary(string? format)
        => string.Equals(format, "summary", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(format, "text", StringComparison.OrdinalIgnoreCase);

    private sealed record StructuredToolEnvelope<TData>(
        int SchemaVersion,
        string Tool,
        bool Success,
        string Summary,
        TData? Data,
        StructuredToolError? Error);

    private sealed record StructuredToolError(
        string Code,
        string Message);
}
