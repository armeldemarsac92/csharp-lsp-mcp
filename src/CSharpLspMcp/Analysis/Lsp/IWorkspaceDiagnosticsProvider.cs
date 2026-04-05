namespace CSharpLspMcp.Analysis.Lsp;

public interface IWorkspaceDiagnosticsProvider
{
    Task<CSharpWorkspaceAnalysisService.WorkspaceDiagnosticsResponse> GetWorkspaceDiagnosticsAsync(
        int maxDocuments,
        int maxDiagnosticsPerDocument,
        string minimumSeverity,
        bool includeGenerated,
        bool includeTests,
        IReadOnlyCollection<string>? excludePaths,
        IReadOnlyCollection<string>? excludeDiagnosticCodes,
        IReadOnlyCollection<string>? excludeDiagnosticSources,
        CancellationToken cancellationToken);
}
