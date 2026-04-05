using System.Text;
using System.Text.Json;
using CSharpLspMcp.Contracts.Common;
using CSharpLspMcp.Lsp;
using CSharpLspMcp.Workspace;

namespace CSharpLspMcp.Analysis.Lsp;

public sealed class CSharpDocumentAnalysisService
{
    private readonly LspClient _lspClient;
    private readonly CSharpWorkspaceSession _workspaceSession;

    public CSharpDocumentAnalysisService(
        LspClient lspClient,
        CSharpWorkspaceSession workspaceSession)
    {
        _lspClient = lspClient;
        _workspaceSession = workspaceSession;
    }

    public async Task<DiagnosticsResponse> GetDiagnosticsAsync(string filePath, string? content, CancellationToken cancellationToken)
    {
        var absolutePath = _workspaceSession.GetAbsolutePath(filePath);
        await _workspaceSession.EnsureDocumentOpenAsync(absolutePath, content, cancellationToken);

        var diagnostics = await _lspClient.WaitForDiagnosticsAsync(
            absolutePath,
            TimeSpan.FromSeconds(5),
            cancellationToken);

        if (diagnostics == null || diagnostics.Diagnostics.Length == 0)
        {
            return new DiagnosticsResponse(
                Summary: "No diagnostics found.",
                FilePath: FormatPath(absolutePath),
                TotalDiagnostics: 0,
                Diagnostics: Array.Empty<DocumentDiagnosticItem>());
        }

        return new DiagnosticsResponse(
            Summary: $"Found {diagnostics.Diagnostics.Length} diagnostic(s).",
            FilePath: FormatPath(absolutePath),
            TotalDiagnostics: diagnostics.Diagnostics.Length,
            Diagnostics: diagnostics.Diagnostics
                .OrderBy(d => d.Range.Start.Line)
                .Select(diag => new DocumentDiagnosticItem(
                    FormatSeverity(diag.Severity),
                    diag.Range.Start.Line + 1,
                    diag.Range.Start.Character + 1,
                    diag.Message,
                    diag.Code?.ToString()))
                .ToArray());
    }

    public async Task<HoverResponse> GetHoverAsync(
        string filePath,
        int line,
        int character,
        string? content,
        CancellationToken cancellationToken)
    {
        var absolutePath = _workspaceSession.GetAbsolutePath(filePath);
        await _workspaceSession.EnsureDocumentOpenAsync(absolutePath, content, cancellationToken);

        var hover = await _lspClient.GetHoverAsync(absolutePath, line, character, cancellationToken);
        if (hover == null)
        {
            return new HoverResponse(
                Summary: "No hover information available at this position.",
                FilePath: FormatPath(absolutePath),
                Line: line + 1,
                Character: character + 1,
                Content: null);
        }

        return new HoverResponse(
            Summary: "Hover information available.",
            FilePath: FormatPath(absolutePath),
            Line: line + 1,
            Character: character + 1,
            Content: FormatHoverContent(hover.Contents));
    }

    public async Task<CompletionsResponse> GetCompletionsAsync(
        string filePath,
        int line,
        int character,
        string? content,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var absolutePath = _workspaceSession.GetAbsolutePath(filePath);
        await _workspaceSession.EnsureDocumentOpenAsync(absolutePath, content, cancellationToken);

        var completions = await _lspClient.GetCompletionsAsync(absolutePath, line, character, cancellationToken);
        if (completions == null || completions.Length == 0)
        {
            return new CompletionsResponse(
                Summary: "No completions available.",
                FilePath: FormatPath(absolutePath),
                Line: line + 1,
                Character: character + 1,
                TotalCompletions: 0,
                Items: Array.Empty<CompletionItem>(),
                TruncatedCompletions: 0);
        }

        var effectiveMaxResults = Math.Max(1, maxResults);
        return new CompletionsResponse(
            Summary: $"Found {completions.Length} completion(s).",
            FilePath: FormatPath(absolutePath),
            Line: line + 1,
            Character: character + 1,
            TotalCompletions: completions.Length,
            Items: completions.Take(effectiveMaxResults)
                .Select(item => new CompletionItem(
                    item.Label,
                    item.Kind?.ToString() ?? "Unknown",
                    item.Detail))
                .ToArray(),
            TruncatedCompletions: Math.Max(0, completions.Length - effectiveMaxResults));
    }

    public async Task<DefinitionResponse> GetDefinitionAsync(
        string filePath,
        int line,
        int character,
        string? content,
        CancellationToken cancellationToken)
    {
        var absolutePath = _workspaceSession.GetAbsolutePath(filePath);
        await _workspaceSession.EnsureDocumentOpenAsync(absolutePath, content, cancellationToken);

        var locations = await _lspClient.GetDefinitionAsync(absolutePath, line, character, cancellationToken);
        if (locations == null || locations.Length == 0)
        {
            return new DefinitionResponse(
                Summary: "No definition found.",
                FilePath: FormatPath(absolutePath),
                Line: line + 1,
                Character: character + 1,
                Definitions: Array.Empty<LocationItem>());
        }

        return new DefinitionResponse(
            Summary: $"Found {locations.Length} definition(s).",
            FilePath: FormatPath(absolutePath),
            Line: line + 1,
            Character: character + 1,
            Definitions: locations.Select(MapLocation).ToArray());
    }

    public async Task<ReferencesResponse> GetReferencesAsync(
        string filePath,
        int line,
        int character,
        string? content,
        bool includeDeclaration,
        CancellationToken cancellationToken)
    {
        var absolutePath = _workspaceSession.GetAbsolutePath(filePath);
        await _workspaceSession.EnsureDocumentOpenAsync(absolutePath, content, cancellationToken);

        var locations = await _lspClient.GetReferencesAsync(
            absolutePath,
            line,
            character,
            includeDeclaration,
            cancellationToken);
        if (locations == null || locations.Length == 0)
        {
            return new ReferencesResponse(
                Summary: "No references found.",
                FilePath: FormatPath(absolutePath),
                Line: line + 1,
                Character: character + 1,
                IncludeDeclaration: includeDeclaration,
                TotalReferences: 0,
                References: Array.Empty<LocationItem>());
        }

        return new ReferencesResponse(
            Summary: $"Found {locations.Length} reference(s).",
            FilePath: FormatPath(absolutePath),
            Line: line + 1,
            Character: character + 1,
            IncludeDeclaration: includeDeclaration,
            TotalReferences: locations.Length,
            References: locations.Select(MapLocation).ToArray());
    }

    public async Task<SymbolsResponse> GetSymbolsAsync(string filePath, string? content, CancellationToken cancellationToken)
    {
        var absolutePath = _workspaceSession.GetAbsolutePath(filePath);
        await _workspaceSession.EnsureDocumentOpenAsync(absolutePath, content, cancellationToken);

        var symbols = await _lspClient.GetDocumentSymbolsAsync(absolutePath, cancellationToken);
        if (symbols == null)
        {
            return new SymbolsResponse(
                Summary: "No symbols found.",
                FilePath: FormatPath(absolutePath),
                TotalSymbols: 0,
                Symbols: Array.Empty<DocumentSymbolItem>());
        }

        if (symbols is DocumentSymbol[] documentSymbols)
        {
            var items = documentSymbols.Select(MapDocumentSymbol).ToArray();
            return new SymbolsResponse(
                Summary: $"Found {CountDocumentSymbols(items)} symbol(s).",
                FilePath: FormatPath(absolutePath),
                TotalSymbols: CountDocumentSymbols(items),
                Symbols: items);
        }

        if (symbols is SymbolInformation[] symbolInformation)
        {
            var items = symbolInformation
                .Select(sym => new DocumentSymbolItem(
                    sym.Name,
                    sym.Kind.ToString(),
                    sym.DetailOrNull(),
                    sym.ContainerName,
                    sym.Location.Range.Start.Line + 1,
                    sym.Location.Range.Start.Character + 1,
                    Array.Empty<DocumentSymbolItem>()))
                .ToArray();
            return new SymbolsResponse(
                Summary: $"Found {items.Length} symbol(s).",
                FilePath: FormatPath(absolutePath),
                TotalSymbols: items.Length,
                Symbols: items);
        }

        return new SymbolsResponse(
            Summary: "No symbols found.",
            FilePath: FormatPath(absolutePath),
            TotalSymbols: 0,
            Symbols: Array.Empty<DocumentSymbolItem>());
    }

    public async Task<CodeActionsResponse> GetCodeActionsAsync(
        string filePath,
        int startLine,
        int startCharacter,
        int endLine,
        int endCharacter,
        string? content,
        CancellationToken cancellationToken)
    {
        var absolutePath = _workspaceSession.GetAbsolutePath(filePath);
        await _workspaceSession.EnsureDocumentOpenAsync(absolutePath, content, cancellationToken);

        var range = new CSharpLspMcp.Lsp.Range
        {
            Start = new Position { Line = startLine, Character = startCharacter },
            End = new Position { Line = endLine, Character = endCharacter }
        };

        var diagnostics = _workspaceSession.GetCachedDiagnostics(absolutePath);
        var relevantDiagnostics = diagnostics?.Diagnostics
            .Where(d => d.Range.Start.Line >= startLine && d.Range.End.Line <= endLine)
            .ToArray() ?? Array.Empty<Diagnostic>();

        var actions = await _lspClient.GetCodeActionsAsync(
            absolutePath,
            range,
            relevantDiagnostics,
            cancellationToken);
        if (actions == null || actions.Length == 0)
        {
            return new CodeActionsResponse(
                Summary: "No code actions available.",
                FilePath: FormatPath(absolutePath),
                Range: new RangeItem(startLine + 1, startCharacter + 1, endLine + 1, endCharacter + 1),
                TotalActions: 0,
                Actions: Array.Empty<CodeActionItem>());
        }

        return new CodeActionsResponse(
            Summary: $"Found {actions.Length} code action(s).",
            FilePath: FormatPath(absolutePath),
            Range: new RangeItem(startLine + 1, startCharacter + 1, endLine + 1, endCharacter + 1),
            TotalActions: actions.Length,
            Actions: actions.Select(action => new CodeActionItem(action.Title, action.Kind)).ToArray());
    }

    public async Task<RenamePreviewResponse> RenameAsync(
        string filePath,
        int line,
        int character,
        string newName,
        string? content,
        CancellationToken cancellationToken)
    {
        var absolutePath = _workspaceSession.GetAbsolutePath(filePath);
        await _workspaceSession.EnsureDocumentOpenAsync(absolutePath, content, cancellationToken);

        var edit = await _lspClient.RenameSymbolAsync(absolutePath, line, character, newName, cancellationToken);
        if (edit == null || edit.Changes == null || edit.Changes.Count == 0)
        {
            return new RenamePreviewResponse(
                Summary: "Cannot rename symbol at this position.",
                FilePath: FormatPath(absolutePath),
                Line: line + 1,
                Character: character + 1,
                NewName: newName,
                TotalEdits: 0,
                TotalFiles: 0,
                Files: Array.Empty<RenameFileEditItem>());
        }

        var files = edit.Changes
            .Select(change => new RenameFileEditItem(
                FormatPath(new Uri(change.Key).LocalPath),
                change.Value.Length))
            .OrderBy(file => file.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new RenamePreviewResponse(
            Summary: $"Rename to '{newName}' would affect {files.Sum(file => file.EditCount)} edit(s) in {files.Length} file(s).",
            FilePath: FormatPath(absolutePath),
            Line: line + 1,
            Character: character + 1,
            NewName: newName,
            TotalEdits: files.Sum(file => file.EditCount),
            TotalFiles: files.Length,
            Files: files);
    }

    internal static string FormatHoverContent(object contents)
    {
        if (contents is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String)
                return element.GetString() ?? "";

            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty("value", out var value))
            {
                return value.GetString() ?? "";
            }

            if (element.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                        sb.AppendLine(item.GetString());
                    else if (item.TryGetProperty("value", out var itemValue))
                        sb.AppendLine(itemValue.GetString());
                }

                return sb.ToString();
            }
        }

        return contents.ToString() ?? "";
    }

    private string FormatPath(string filePath)
    {
        var workspacePath = _workspaceSession.WorkspacePath;
        if (string.IsNullOrWhiteSpace(workspacePath))
            return filePath;

        try
        {
            return Path.GetRelativePath(workspacePath, filePath);
        }
        catch (ArgumentException)
        {
            return filePath;
        }
    }

    private static string FormatSeverity(DiagnosticSeverity? severity)
        => severity switch
        {
            DiagnosticSeverity.Error => "ERROR",
            DiagnosticSeverity.Warning => "WARNING",
            DiagnosticSeverity.Information => "INFO",
            DiagnosticSeverity.Hint => "HINT",
            _ => "UNKNOWN"
        };

    private LocationItem MapLocation(Location location)
        => new(
            FormatPath(new Uri(location.Uri).LocalPath),
            location.Range.Start.Line + 1,
            location.Range.Start.Character + 1);

    private static DocumentSymbolItem MapDocumentSymbol(DocumentSymbol symbol)
        => new(
            symbol.Name,
            symbol.Kind.ToString(),
            symbol.Detail,
            null,
            symbol.Range.Start.Line + 1,
            symbol.Range.Start.Character + 1,
            symbol.Children?.Select(MapDocumentSymbol).ToArray() ?? Array.Empty<DocumentSymbolItem>());

    private static int CountDocumentSymbols(IEnumerable<DocumentSymbolItem> symbols)
        => symbols.Sum(symbol => 1 + CountDocumentSymbols(symbol.Children));

    public sealed record DiagnosticsResponse(
        string Summary,
        string FilePath,
        int TotalDiagnostics,
        DocumentDiagnosticItem[] Diagnostics) : IStructuredToolResult;

    public sealed record DocumentDiagnosticItem(
        string Severity,
        int Line,
        int Character,
        string Message,
        string? Code);

    public sealed record HoverResponse(
        string Summary,
        string FilePath,
        int Line,
        int Character,
        string? Content) : IStructuredToolResult;

    public sealed record CompletionsResponse(
        string Summary,
        string FilePath,
        int Line,
        int Character,
        int TotalCompletions,
        CompletionItem[] Items,
        int TruncatedCompletions) : IStructuredToolResult;

    public sealed record CompletionItem(
        string Label,
        string Kind,
        string? Detail);

    public sealed record DefinitionResponse(
        string Summary,
        string FilePath,
        int Line,
        int Character,
        LocationItem[] Definitions) : IStructuredToolResult;

    public sealed record ReferencesResponse(
        string Summary,
        string FilePath,
        int Line,
        int Character,
        bool IncludeDeclaration,
        int TotalReferences,
        LocationItem[] References) : IStructuredToolResult;

    public sealed record LocationItem(
        string FilePath,
        int Line,
        int Character);

    public sealed record SymbolsResponse(
        string Summary,
        string FilePath,
        int TotalSymbols,
        DocumentSymbolItem[] Symbols) : IStructuredToolResult;

    public sealed record DocumentSymbolItem(
        string Name,
        string Kind,
        string? Detail,
        string? ContainerName,
        int Line,
        int Character,
        DocumentSymbolItem[] Children);

    public sealed record CodeActionsResponse(
        string Summary,
        string FilePath,
        RangeItem Range,
        int TotalActions,
        CodeActionItem[] Actions) : IStructuredToolResult;

    public sealed record CodeActionItem(
        string Title,
        string? Kind);

    public sealed record RenamePreviewResponse(
        string Summary,
        string FilePath,
        int Line,
        int Character,
        string NewName,
        int TotalEdits,
        int TotalFiles,
        RenameFileEditItem[] Files) : IStructuredToolResult;

    public sealed record RenameFileEditItem(
        string FilePath,
        int EditCount);

    public sealed record RangeItem(
        int StartLine,
        int StartCharacter,
        int EndLine,
        int EndCharacter);
}

internal static class SymbolInformationExtensions
{
    public static string? DetailOrNull(this SymbolInformation symbol)
        => null;
}
