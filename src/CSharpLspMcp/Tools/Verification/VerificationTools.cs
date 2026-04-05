using System.ComponentModel;
using CSharpLspMcp.Analysis.Verification;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CSharpLspMcp.Tools.Verification;

[McpServerToolType]
public sealed class VerificationTools : CSharpToolBase
{
    private readonly CSharpChangeVerificationService _changeVerificationService;
    private readonly ILogger<VerificationTools> _logger;

    public VerificationTools(
        ILogger<VerificationTools> logger,
        CSharpChangeVerificationService changeVerificationService)
    {
        _logger = logger;
        _changeVerificationService = changeVerificationService;
    }

    [McpServerTool(Name = "csharp_verify_change")]
    [Description("Prepare a verification plan for changed symbols or files: build commands, test commands, focused diagnostics, and ordered verification steps.")]
    public Task<string> VerifyChangeAsync(
        [Description("Optional short description of the intended change.")] string? request = null,
        [Description("Fully qualified symbol name, documentation ID, or simple symbol query.")] string? symbolQuery = null,
        [Description("Optional file path to analyze instead of or in addition to a symbol query.")] string? filePath = null,
        [Description("Optional changed files to prioritize in diagnostics and project selection.")] string[]? changedFiles = null,
        [Description("Include test projects and test diagnostics in the verification plan (default: true).")] bool includeTests = true,
        [Description("Rebuild the graph automatically when it is missing or stale for impact planning (default: true).")] bool rebuildIfMissing = true,
        [Description("Minimum severity to include in focused diagnostics: ALL, ERROR, WARNING (default), INFO, or HINT.")] string minimumSeverity = "WARNING",
        [Description("Optional diagnostic codes to exclude, such as IDE0005 or CS8933.")] string[]? excludeDiagnosticCodes = null,
        [Description("Optional diagnostic sources to exclude, such as csharp or Style.")] string[]? excludeDiagnosticSources = null,
        [Description("Maximum number of items to include per section (default: 10).")] int maxResults = 10,
        [Description("Output format: structured (default) or summary.")] string format = "structured",
        CancellationToken cancellationToken = default)
        => ExecuteStructuredToolAsync(
            _logger,
            "csharp_verify_change",
            format,
            ct => _changeVerificationService.VerifyAsync(
                request,
                symbolQuery,
                filePath,
                changedFiles,
                includeTests,
                rebuildIfMissing,
                minimumSeverity,
                excludeDiagnosticCodes,
                excludeDiagnosticSources,
                maxResults,
                ct),
            cancellationToken);
}
