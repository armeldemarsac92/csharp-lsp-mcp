using CSharpLspMcp.Contracts.Common;
using CSharpLspMcp.Lsp;
using CSharpLspMcp.Workspace;

namespace CSharpLspMcp.Analysis.Lsp;

public sealed class CSharpWorkspaceAnalysisService
{
    private static readonly string[] GeneratedFileSuffixes = [".g.cs", ".g.i.cs", ".designer.cs", ".generated.cs", ".AssemblyInfo.cs"];
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
        string minimumSeverity,
        bool includeGenerated,
        bool includeTests,
        IReadOnlyCollection<string>? excludePaths,
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
                MinimumSeverity: NormalizeMinimumSeverity(minimumSeverity),
                IncludeGenerated: includeGenerated,
                IncludeTests: includeTests,
                ExcludedPathPatterns: excludePaths?.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray() ?? Array.Empty<string>(),
                TotalDiagnostics: 0,
                TotalDocuments: 0,
                Documents: Array.Empty<WorkspaceDiagnosticDocument>(),
                TruncatedDocuments: 0);
        }

        var effectiveMinimumSeverity = NormalizeMinimumSeverity(minimumSeverity);
        var excludedPathPatterns = excludePaths?
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();
        var documentsWithDiagnostics = report.Items
            .Where(item => string.Equals(item.Kind, "full", StringComparison.OrdinalIgnoreCase))
            .Select(item => new
            {
                Item = item,
                FilePath = new Uri(item.Uri).LocalPath,
                Diagnostics = FilterDiagnostics(item.Diagnostics ?? Array.Empty<Diagnostic>(), effectiveMinimumSeverity)
            })
            .Where(item => ShouldIncludeDocument(item.FilePath, includeGenerated, includeTests, excludedPathPatterns))
            .Where(item => item.Diagnostics.Length > 0)
            .OrderByDescending(item => item.Diagnostics.Length)
            .ToArray();

        if (documentsWithDiagnostics.Length == 0)
        {
            return new WorkspaceDiagnosticsResponse(
                Summary: "No workspace diagnostics found.",
                SolutionRoot: workspacePath,
                MinimumSeverity: effectiveMinimumSeverity,
                IncludeGenerated: includeGenerated,
                IncludeTests: includeTests,
                ExcludedPathPatterns: excludedPathPatterns,
                TotalDiagnostics: 0,
                TotalDocuments: 0,
                Documents: Array.Empty<WorkspaceDiagnosticDocument>(),
                TruncatedDocuments: 0);
        }

        var totalDiagnostics = documentsWithDiagnostics.Sum(item => item.Diagnostics.Length);
        var effectiveMaxDocuments = Math.Max(1, maxDocuments);
        var effectiveMaxDiagnostics = Math.Max(1, maxDiagnosticsPerDocument);
        return new WorkspaceDiagnosticsResponse(
            Summary: BuildWorkspaceSummary(totalDiagnostics, documentsWithDiagnostics.Length, effectiveMinimumSeverity, includeGenerated, includeTests, excludedPathPatterns),
            SolutionRoot: workspacePath,
            MinimumSeverity: effectiveMinimumSeverity,
            IncludeGenerated: includeGenerated,
            IncludeTests: includeTests,
            ExcludedPathPatterns: excludedPathPatterns,
            TotalDiagnostics: totalDiagnostics,
            TotalDocuments: documentsWithDiagnostics.Length,
            Documents: documentsWithDiagnostics.Take(effectiveMaxDocuments)
                .Select(document => new WorkspaceDiagnosticDocument(
                    document.FilePath,
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

    private static Diagnostic[] FilterDiagnostics(
        IReadOnlyCollection<Diagnostic> diagnostics,
        string minimumSeverity)
    {
        var threshold = GetSeverityThreshold(minimumSeverity);
        return diagnostics
            .Where(diagnostic => GetSeverityThreshold(FormatSeverity(diagnostic.Severity)) >= threshold)
            .ToArray();
    }

    private static bool ShouldIncludeDocument(
        string filePath,
        bool includeGenerated,
        bool includeTests,
        IReadOnlyCollection<string> excludePaths)
    {
        if (!includeGenerated && IsGeneratedPath(filePath))
            return false;

        if (!includeTests && IsTestPath(filePath))
            return false;

        return !excludePaths.Any(pattern => filePath.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsGeneratedPath(string filePath)
    {
        if (GeneratedFileSuffixes.Any(suffix => filePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            return true;

        var segments = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment =>
            string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(segment, "Generated", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTestPath(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        if (fileName.Contains("Test", StringComparison.OrdinalIgnoreCase))
            return true;

        var segments = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment =>
            string.Equals(segment, "test", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(segment, "tests", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeMinimumSeverity(string minimumSeverity)
    {
        if (string.IsNullOrWhiteSpace(minimumSeverity))
            return "WARNING";

        return minimumSeverity.Trim().ToUpperInvariant() switch
        {
            "ALL" => "ALL",
            "ERROR" => "ERROR",
            "WARNING" => "WARNING",
            "INFO" => "INFO",
            "INFORMATION" => "INFO",
            "HINT" => "HINT",
            _ => "WARNING"
        };
    }

    private static int GetSeverityThreshold(string severity)
    {
        return severity switch
        {
            "ERROR" => 4,
            "WARNING" => 3,
            "INFO" => 2,
            "HINT" => 1,
            "ALL" => 0,
            _ => 3
        };
    }

    private static string BuildWorkspaceSummary(
        int totalDiagnostics,
        int totalDocuments,
        string minimumSeverity,
        bool includeGenerated,
        bool includeTests,
        IReadOnlyCollection<string> excludedPathPatterns)
    {
        var clauses = new List<string> { $"minimum severity {minimumSeverity}" };
        if (!includeGenerated)
            clauses.Add("generated files excluded");
        if (!includeTests)
            clauses.Add("test files excluded");
        if (excludedPathPatterns.Count > 0)
            clauses.Add($"{excludedPathPatterns.Count} path exclusion(s)");

        return $"Found {totalDiagnostics} diagnostic(s) across {totalDocuments} document(s) with {string.Join(", ", clauses)}.";
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
        string MinimumSeverity,
        bool IncludeGenerated,
        bool IncludeTests,
        string[] ExcludedPathPatterns,
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
