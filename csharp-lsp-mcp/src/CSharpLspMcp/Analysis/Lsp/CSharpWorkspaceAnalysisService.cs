using CSharpLspMcp.Contracts.Common;
using CSharpLspMcp.Lsp;
using CSharpLspMcp.Workspace;

namespace CSharpLspMcp.Analysis.Lsp;

public sealed class CSharpWorkspaceAnalysisService
{
    private readonly LspClient _lspClient;
    private readonly CSharpWorkspaceSession _workspaceSession;

    public CSharpWorkspaceAnalysisService(
        LspClient lspClient,
        CSharpWorkspaceSession workspaceSession)
    {
        _lspClient = lspClient;
        _workspaceSession = workspaceSession;
    }

    public async Task<WorkspaceDiagnosticsResponse> GetWorkspaceDiagnosticsAsync(
        int maxDocuments,
        int maxDiagnosticsPerDocument,
        CancellationToken cancellationToken)
    {
        var workspacePath = _workspaceSession.WorkspacePath;
        if (string.IsNullOrWhiteSpace(workspacePath))
            throw new InvalidOperationException("Workspace is not set. Call csharp_set_workspace first.");

        if (!_workspaceSession.IsRunning)
        {
            var started = await _workspaceSession.SetWorkspaceAsync(workspacePath, cancellationToken);
            if (!started)
                throw new InvalidOperationException($"Failed to start LSP server for workspace: {workspacePath}");
        }

        var report = await _lspClient.GetWorkspaceDiagnosticsAsync(cancellationToken);
        if (report == null || report.Items.Length == 0)
        {
            return new WorkspaceDiagnosticsResponse(
                Summary: "No workspace diagnostics found.",
                SolutionRoot: workspacePath,
                TotalDiagnostics: 0,
                TotalDocuments: 0,
                Documents: Array.Empty<WorkspaceDiagnosticDocument>(),
                TruncatedDocuments: 0);
        }

        var documentsWithDiagnostics = report.Items
            .Where(item => string.Equals(item.Kind, "full", StringComparison.OrdinalIgnoreCase))
            .Select(item => new
            {
                Item = item,
                Diagnostics = item.Diagnostics ?? Array.Empty<Diagnostic>()
            })
            .Where(item => item.Diagnostics.Length > 0)
            .OrderByDescending(item => item.Diagnostics.Length)
            .ToArray();

        if (documentsWithDiagnostics.Length == 0)
        {
            return new WorkspaceDiagnosticsResponse(
                Summary: "No workspace diagnostics found.",
                SolutionRoot: workspacePath,
                TotalDiagnostics: 0,
                TotalDocuments: 0,
                Documents: Array.Empty<WorkspaceDiagnosticDocument>(),
                TruncatedDocuments: 0);
        }

        var totalDiagnostics = documentsWithDiagnostics.Sum(item => item.Diagnostics.Length);
        var effectiveMaxDocuments = Math.Max(1, maxDocuments);
        var effectiveMaxDiagnostics = Math.Max(1, maxDiagnosticsPerDocument);
        return new WorkspaceDiagnosticsResponse(
            Summary: $"Found {totalDiagnostics} diagnostic(s) across {documentsWithDiagnostics.Length} document(s).",
            SolutionRoot: workspacePath,
            TotalDiagnostics: totalDiagnostics,
            TotalDocuments: documentsWithDiagnostics.Length,
            Documents: documentsWithDiagnostics.Take(effectiveMaxDocuments)
                .Select(document => new WorkspaceDiagnosticDocument(
                    new Uri(document.Item.Uri).LocalPath,
                    document.Diagnostics.Length,
                    document.Diagnostics.OrderBy(diag => diag.Range.Start.Line)
                        .Take(effectiveMaxDiagnostics)
                        .Select(diagnostic => new WorkspaceDiagnosticItem(
                            FormatSeverity(diagnostic.Severity),
                            diagnostic.Range.Start.Line + 1,
                            diagnostic.Range.Start.Character + 1,
                            diagnostic.Message))
                        .ToArray(),
                    Math.Max(0, document.Diagnostics.Length - effectiveMaxDiagnostics)))
                .ToArray(),
            TruncatedDocuments: Math.Max(0, documentsWithDiagnostics.Length - effectiveMaxDocuments));
    }

    private static string FormatSeverity(DiagnosticSeverity? severity)
    {
        return severity switch
        {
            DiagnosticSeverity.Error => "ERROR",
            DiagnosticSeverity.Warning => "WARNING",
            DiagnosticSeverity.Information => "INFO",
            DiagnosticSeverity.Hint => "HINT",
            _ => "UNKNOWN"
        };
    }

    public sealed record WorkspaceDiagnosticsResponse(
        string Summary,
        string SolutionRoot,
        int TotalDiagnostics,
        int TotalDocuments,
        WorkspaceDiagnosticDocument[] Documents,
        int TruncatedDocuments) : IStructuredToolResult;

    public sealed record WorkspaceDiagnosticDocument(
        string FilePath,
        int DiagnosticCount,
        WorkspaceDiagnosticItem[] Diagnostics,
        int TruncatedDiagnostics);

    public sealed record WorkspaceDiagnosticItem(
        string Severity,
        int Line,
        int Character,
        string Message);
}
