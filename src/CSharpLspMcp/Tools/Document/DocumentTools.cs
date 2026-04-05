using System.ComponentModel;
using CSharpLspMcp.Analysis.Lsp;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CSharpLspMcp.Tools.Document;

[McpServerToolType]
public sealed class DocumentTools : CSharpToolBase
{
    private readonly ILogger<DocumentTools> _logger;
    private readonly CSharpDocumentAnalysisService _documentAnalysisService;

    public DocumentTools(
        ILogger<DocumentTools> logger,
        CSharpDocumentAnalysisService documentAnalysisService)
    {
        _logger = logger;
        _documentAnalysisService = documentAnalysisService;
    }

    [McpServerTool(Name = "csharp_diagnostics")]
    [Description("Get compiler errors and warnings for C# code. Opens the document in the LSP if not already open.")]
    public Task<string> GetDiagnosticsAsync(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("Content of the file (optional, reads from disk if not provided)")] string? content = null,
        [Description("Output format: structured (default) or summary.")] string format = "structured",
        CancellationToken cancellationToken = default)
        => ExecuteStructuredToolAsync(
            _logger,
            "csharp_diagnostics",
            format,
            ct => _documentAnalysisService.GetDiagnosticsAsync(filePath, content, ct),
            cancellationToken);

    [McpServerTool(Name = "csharp_hover")]
    [Description("Get type information and documentation at a specific position in C# code")]
    public Task<string> GetHoverAsync(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based character position")] int character,
        [Description("Content of the file (optional)")] string? content = null,
        [Description("Output format: structured (default) or summary.")] string format = "structured",
        CancellationToken cancellationToken = default)
        => ExecuteStructuredToolAsync(
            _logger,
            "csharp_hover",
            format,
            ct => _documentAnalysisService.GetHoverAsync(filePath, line, character, content, ct),
            cancellationToken);

    [McpServerTool(Name = "csharp_completions")]
    [Description("Get IntelliSense completions at a specific position")]
    public Task<string> GetCompletionsAsync(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based character position")] int character,
        [Description("Content of the file (optional)")] string? content = null,
        [Description("Maximum number of completions to return (default: 20)")] int maxResults = 20,
        [Description("Output format: structured (default) or summary.")] string format = "structured",
        CancellationToken cancellationToken = default)
        => ExecuteStructuredToolAsync(
            _logger,
            "csharp_completions",
            format,
            ct => _documentAnalysisService.GetCompletionsAsync(filePath, line, character, content, maxResults, ct),
            cancellationToken);

    [McpServerTool(Name = "csharp_definition")]
    [Description("Go to definition - find where a symbol is defined")]
    public Task<string> GetDefinitionAsync(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based character position")] int character,
        [Description("Content of the file (optional)")] string? content = null,
        [Description("Output format: structured (default) or summary.")] string format = "structured",
        CancellationToken cancellationToken = default)
        => ExecuteStructuredToolAsync(
            _logger,
            "csharp_definition",
            format,
            ct => _documentAnalysisService.GetDefinitionAsync(filePath, line, character, content, ct),
            cancellationToken);

    [McpServerTool(Name = "csharp_references")]
    [Description("Find all references to a symbol")]
    public Task<string> GetReferencesAsync(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based character position")] int character,
        [Description("Content of the file (optional)")] string? content = null,
        [Description("Include the declaration in results (default: true)")] bool includeDeclaration = true,
        [Description("Output format: structured (default) or summary.")] string format = "structured",
        CancellationToken cancellationToken = default)
        => ExecuteStructuredToolAsync(
            _logger,
            "csharp_references",
            format,
            ct => _documentAnalysisService.GetReferencesAsync(filePath, line, character, content, includeDeclaration, ct),
            cancellationToken);

    [McpServerTool(Name = "csharp_symbols")]
    [Description("Get all symbols (classes, methods, properties, etc.) in a document")]
    public Task<string> GetSymbolsAsync(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("Content of the file (optional)")] string? content = null,
        [Description("Output format: structured (default) or summary.")] string format = "structured",
        CancellationToken cancellationToken = default)
        => ExecuteStructuredToolAsync(
            _logger,
            "csharp_symbols",
            format,
            ct => _documentAnalysisService.GetSymbolsAsync(filePath, content, ct),
            cancellationToken);

    [McpServerTool(Name = "csharp_code_actions")]
    [Description("Get available code actions (quick fixes, refactorings) for a range")]
    public Task<string> GetCodeActionsAsync(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("0-based start line")] int startLine,
        [Description("0-based start character")] int startCharacter,
        [Description("0-based end line")] int endLine,
        [Description("0-based end character")] int endCharacter,
        [Description("Content of the file (optional)")] string? content = null,
        [Description("Output format: structured (default) or summary.")] string format = "structured",
        CancellationToken cancellationToken = default)
        => ExecuteStructuredToolAsync(
            _logger,
            "csharp_code_actions",
            format,
            ct => _documentAnalysisService.GetCodeActionsAsync(
                filePath,
                startLine,
                startCharacter,
                endLine,
                endCharacter,
                content,
                ct),
            cancellationToken);

    [McpServerTool(Name = "csharp_rename")]
    [Description("Rename a symbol across the workspace")]
    public Task<string> RenameAsync(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based character position")] int character,
        [Description("The new name for the symbol")] string newName,
        [Description("Content of the file (optional)")] string? content = null,
        [Description("Output format: structured (default) or summary.")] string format = "structured",
        CancellationToken cancellationToken = default)
        => ExecuteStructuredToolAsync(
            _logger,
            "csharp_rename",
            format,
            ct => _documentAnalysisService.RenameAsync(filePath, line, character, newName, content, ct),
            cancellationToken);
}
