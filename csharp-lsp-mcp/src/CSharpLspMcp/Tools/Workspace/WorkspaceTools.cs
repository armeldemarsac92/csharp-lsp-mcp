using System.ComponentModel;
using CSharpLspMcp.Contracts.Common;
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
        [Description("Output format: structured (default) or summary.")] string format = "structured",
        CancellationToken cancellationToken = default)
    {
        return ExecuteStructuredToolAsync(_logger, "csharp_set_workspace", format, async ct =>
        {
            if (!Directory.Exists(path))
                throw new InvalidOperationException($"Directory does not exist: {path}");

            var started = await _workspaceSession.SetWorkspaceAsync(path, ct);
            if (!started)
                throw new InvalidOperationException("Failed to start LSP server. Make sure csharp-ls is installed: dotnet tool install --global csharp-ls");

            return new SimpleStructuredResult(
                Summary: $"Workspace set to {path}.",
                Message: "LSP server started successfully.");
        }, cancellationToken);
    }

    [McpServerTool(Name = "csharp_stop")]
    [Description("Stop the C# LSP server to release file locks. Call this before rebuilding your project. Use csharp_set_workspace to restart it afterwards.")]
    public Task<string> StopAsync(
        [Description("Output format: structured (default) or summary.")] string format = "structured",
        CancellationToken cancellationToken = default)
    {
        return ExecuteStructuredToolAsync(_logger, "csharp_stop", format, async _ =>
        {
            if (!_workspaceSession.IsRunning)
                return new SimpleStructuredResult("LSP server is not running.");

            await _workspaceSession.StopAsync();
            return new SimpleStructuredResult(
                Summary: "LSP server stopped.",
                Message: "File locks released. Call csharp_set_workspace to restart when ready.");
        }, cancellationToken);
    }

    [McpServerTool(Name = "csharp_workspace_diagnostics")]
    [Description("Get diagnostics across the current workspace or solution using LSP pull diagnostics when supported.")]
    public Task<string> GetWorkspaceDiagnosticsAsync(
        [Description("Maximum number of documents to include in the response (default: 20)")] int maxDocuments = 20,
        [Description("Maximum number of diagnostics to show per document (default: 10)")] int maxDiagnosticsPerDocument = 10,
        [Description("Minimum severity to include: ALL, ERROR, WARNING (default), INFO, or HINT.")] string minimumSeverity = "WARNING",
        [Description("Include generated files such as obj, bin, and *.g.cs outputs (default: false).")] bool includeGenerated = false,
        [Description("Include test files and test projects in the results (default: true).")] bool includeTests = true,
        [Description("Optional path substrings to exclude from the results.")] string[]? excludePaths = null,
        [Description("Optional diagnostic codes to exclude, such as IDE0005 or CS8933.")] string[]? excludeDiagnosticCodes = null,
        [Description("Optional diagnostic sources to exclude, such as csharp or Style.")] string[]? excludeDiagnosticSources = null,
        [Description("Output format: structured (default) or summary.")] string format = "structured",
        CancellationToken cancellationToken = default)
        => ExecuteStructuredToolAsync(
            _logger,
            "csharp_workspace_diagnostics",
            format,
            ct => _workspaceAnalysisService.GetWorkspaceDiagnosticsAsync(
                maxDocuments,
                maxDiagnosticsPerDocument,
                minimumSeverity,
                includeGenerated,
                includeTests,
                excludePaths,
                excludeDiagnosticCodes,
                excludeDiagnosticSources,
                ct),
            cancellationToken);
}
