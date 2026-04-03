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
}
