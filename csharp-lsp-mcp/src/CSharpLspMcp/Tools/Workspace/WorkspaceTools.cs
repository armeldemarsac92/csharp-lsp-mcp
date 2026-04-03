using System.ComponentModel;
using CSharpLspMcp.Analysis.Lsp;
using CSharpLspMcp.Workspace;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CSharpLspMcp.Tools.Workspace;

[McpServerToolType]
public sealed class WorkspaceTools : CSharpToolBase
{
    private readonly ILogger<WorkspaceTools> _logger;
    private readonly CSharpWorkspaceAnalysisService _workspaceAnalysisService;
    private readonly CSharpWorkspaceSession _workspaceSession;

    public WorkspaceTools(
        ILogger<WorkspaceTools> logger,
        CSharpWorkspaceAnalysisService workspaceAnalysisService,
        CSharpWorkspaceSession workspaceSession)
    {
        _logger = logger;
        _workspaceAnalysisService = workspaceAnalysisService;
        _workspaceSession = workspaceSession;
    }

    [McpServerTool(Name = "csharp_set_workspace")]
    [Description("Set the workspace/solution directory for the C# LSP. Call this first before other operations.")]
    public Task<string> SetWorkspaceAsync(
        [Description("Path to the solution/project directory")] string path,
        CancellationToken cancellationToken)
    {
        return ExecuteToolAsync(_logger, "csharp_set_workspace", async ct =>
        {
            if (!Directory.Exists(path))
                return $"Error: Directory does not exist: {path}";

            var started = await _workspaceSession.SetWorkspaceAsync(path, ct);
            if (!started)
                return "Error: Failed to start LSP server. Make sure csharp-ls is installed: dotnet tool install --global csharp-ls";

            return $"Workspace set to: {path}\nLSP server started successfully.";
        }, cancellationToken);
    }

    [McpServerTool(Name = "csharp_stop")]
    [Description("Stop the C# LSP server to release file locks. Call this before rebuilding your project. Use csharp_set_workspace to restart it afterwards.")]
    public Task<string> StopAsync(CancellationToken cancellationToken)
    {
        return ExecuteToolAsync(_logger, "csharp_stop", async _ =>
        {
            if (!_workspaceSession.IsRunning)
                return "LSP server is not running.";

            await _workspaceSession.StopAsync();
            return "LSP server stopped. File locks released.\nCall csharp_set_workspace to restart when ready.";
        }, cancellationToken);
    }

    [McpServerTool(Name = "csharp_workspace_diagnostics")]
    [Description("Get diagnostics across the current workspace or solution using LSP pull diagnostics when supported.")]
    public Task<string> GetWorkspaceDiagnosticsAsync(
        [Description("Maximum number of documents to include in the response (default: 20)")] int maxDocuments = 20,
        [Description("Maximum number of diagnostics to show per document (default: 10)")] int maxDiagnosticsPerDocument = 10,
        CancellationToken cancellationToken = default)
        => ExecuteToolAsync(
            _logger,
            "csharp_workspace_diagnostics",
            ct => _workspaceAnalysisService.GetWorkspaceDiagnosticsAsync(maxDocuments, maxDiagnosticsPerDocument, ct),
            cancellationToken);
}
