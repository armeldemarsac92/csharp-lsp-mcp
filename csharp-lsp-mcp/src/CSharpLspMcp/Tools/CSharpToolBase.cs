using CSharpLspMcp.Contracts.Common;
using CSharpLspMcp.Formatting;
using Microsoft.Extensions.Logging;

namespace CSharpLspMcp.Tools;

public abstract class CSharpToolBase
{
    private static readonly TimeSpan ToolOperationTimeout = TimeSpan.FromMinutes(3);

    protected static async Task<string> ExecuteToolAsync(
        ILogger logger,
        string toolName,
        Func<CancellationToken, Task<string>> action,
        CancellationToken mcpToken)
    {
        using var cts = new CancellationTokenSource(ToolOperationTimeout);

        try
        {
            return await action(cts.Token);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(ex, "Tool {ToolName} was cancelled", toolName);
            if (mcpToken.IsCancellationRequested)
                return "Error: Operation was cancelled by the client.";

            return $"Error: Operation timed out after {ToolOperationTimeout.TotalSeconds} seconds.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing tool {ToolName}", toolName);
            return $"Error: {ex.Message}";
        }
    }

    protected static async Task<string> ExecuteStructuredToolAsync<TResult>(
        ILogger logger,
        string toolName,
        string? format,
        Func<CancellationToken, Task<TResult>> action,
        CancellationToken mcpToken)
        where TResult : IStructuredToolResult
    {
        using var cts = new CancellationTokenSource(ToolOperationTimeout);

        try
        {
            var result = await action(cts.Token);
            return StructuredToolOutputFormatter.FormatSuccess(toolName, format, result);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(ex, "Tool {ToolName} was cancelled", toolName);
            var message = mcpToken.IsCancellationRequested
                ? "Operation was cancelled by the client."
                : $"Operation timed out after {ToolOperationTimeout.TotalSeconds} seconds.";
            var code = mcpToken.IsCancellationRequested ? "cancelled" : "timeout";
            return StructuredToolOutputFormatter.FormatError(toolName, format, code, message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing tool {ToolName}", toolName);
            return StructuredToolOutputFormatter.FormatError(toolName, format, "tool_error", ex.Message);
        }
    }
}
