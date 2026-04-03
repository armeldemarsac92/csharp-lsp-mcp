using System.Text;
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

    public async Task<string> GetWorkspaceDiagnosticsAsync(
        int maxDocuments,
        int maxDiagnosticsPerDocument,
        CancellationToken cancellationToken)
    {
        var workspacePath = _workspaceSession.WorkspacePath;
        if (string.IsNullOrWhiteSpace(workspacePath))
            return "Error: Workspace is not set. Call csharp_set_workspace first.";

        if (!_workspaceSession.IsRunning)
        {
            var started = await _workspaceSession.SetWorkspaceAsync(workspacePath, cancellationToken);
            if (!started)
                return $"Error: Failed to start LSP server for workspace: {workspacePath}";
        }

        var report = await _lspClient.GetWorkspaceDiagnosticsAsync(cancellationToken);
        if (report == null || report.Items.Length == 0)
            return "No workspace diagnostics found.";

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
            return "No workspace diagnostics found.";

        var totalDiagnostics = documentsWithDiagnostics.Sum(item => item.Diagnostics.Length);
        var sb = new StringBuilder();
        sb.AppendLine($"Found {totalDiagnostics} diagnostic(s) across {documentsWithDiagnostics.Length} document(s):\n");

        foreach (var document in documentsWithDiagnostics.Take(maxDocuments))
        {
            var path = new Uri(document.Item.Uri).LocalPath;
            sb.AppendLine($"• {path} ({document.Diagnostics.Length})");

            foreach (var diagnostic in document.Diagnostics
                         .OrderBy(diag => diag.Range.Start.Line)
                         .Take(maxDiagnosticsPerDocument))
            {
                sb.AppendLine(
                    $"  [{FormatSeverity(diagnostic.Severity)}] {diagnostic.Range.Start.Line + 1}:{diagnostic.Range.Start.Character + 1} {diagnostic.Message}");
            }

            if (document.Diagnostics.Length > maxDiagnosticsPerDocument)
                sb.AppendLine($"  ... and {document.Diagnostics.Length - maxDiagnosticsPerDocument} more");
        }

        if (documentsWithDiagnostics.Length > maxDocuments)
            sb.AppendLine($"\n... and {documentsWithDiagnostics.Length - maxDocuments} more document(s)");

        return sb.ToString();
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
}
