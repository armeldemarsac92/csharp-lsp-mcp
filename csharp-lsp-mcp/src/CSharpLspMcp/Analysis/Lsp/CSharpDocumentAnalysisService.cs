using System.Text;
using System.Text.Json;
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

    public async Task<string> GetDiagnosticsAsync(string filePath, string? content, CancellationToken cancellationToken)
    {
        var absolutePath = _workspaceSession.GetAbsolutePath(filePath);
        await _workspaceSession.EnsureDocumentOpenAsync(absolutePath, content, cancellationToken);

        var diagnostics = await _lspClient.WaitForDiagnosticsAsync(
            absolutePath,
            TimeSpan.FromSeconds(5),
            cancellationToken);

        if (diagnostics == null || diagnostics.Diagnostics.Length == 0)
            return "No diagnostics found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {diagnostics.Diagnostics.Length} diagnostic(s):\n");

        foreach (var diag in diagnostics.Diagnostics.OrderBy(d => d.Range.Start.Line))
        {
            var severity = diag.Severity switch
            {
                DiagnosticSeverity.Error => "ERROR",
                DiagnosticSeverity.Warning => "WARNING",
                DiagnosticSeverity.Information => "INFO",
                DiagnosticSeverity.Hint => "HINT",
                _ => "UNKNOWN"
            };

            sb.AppendLine($"[{severity}] Line {diag.Range.Start.Line + 1}, Col {diag.Range.Start.Character + 1}:");
            sb.AppendLine($"  {diag.Message}");
            if (diag.Code != null)
                sb.AppendLine($"  Code: {diag.Code}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public async Task<string> GetHoverAsync(
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
            return "No hover information available at this position.";

        return FormatHoverContent(hover.Contents);
    }

    public async Task<string> GetCompletionsAsync(
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
            return "No completions available.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {completions.Length} completion(s):\n");

        foreach (var item in completions.Take(maxResults))
        {
            var kind = item.Kind?.ToString() ?? "Unknown";
            sb.AppendLine($"• {item.Label} ({kind})");
            if (!string.IsNullOrEmpty(item.Detail))
                sb.AppendLine($"  {item.Detail}");
        }

        if (completions.Length > maxResults)
            sb.AppendLine($"\n... and {completions.Length - maxResults} more");

        return sb.ToString();
    }

    public async Task<string> GetDefinitionAsync(
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
            return "No definition found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {locations.Length} definition(s):\n");

        foreach (var loc in locations)
        {
            var path = new Uri(loc.Uri).LocalPath;
            sb.AppendLine($"• {path}");
            sb.AppendLine($"  Line {loc.Range.Start.Line + 1}, Col {loc.Range.Start.Character + 1}");
        }

        return sb.ToString();
    }

    public async Task<string> GetReferencesAsync(
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
            return "No references found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {locations.Length} reference(s):\n");

        var grouped = locations.GroupBy(l => new Uri(l.Uri).LocalPath);
        foreach (var group in grouped)
        {
            sb.AppendLine($"• {group.Key}");
            foreach (var loc in group.OrderBy(l => l.Range.Start.Line))
                sb.AppendLine($"  Line {loc.Range.Start.Line + 1}, Col {loc.Range.Start.Character + 1}");
        }

        return sb.ToString();
    }

    public async Task<string> GetSymbolsAsync(string filePath, string? content, CancellationToken cancellationToken)
    {
        var absolutePath = _workspaceSession.GetAbsolutePath(filePath);
        await _workspaceSession.EnsureDocumentOpenAsync(absolutePath, content, cancellationToken);

        var symbols = await _lspClient.GetDocumentSymbolsAsync(absolutePath, cancellationToken);
        if (symbols == null)
            return "No symbols found.";

        var sb = new StringBuilder();

        if (symbols is DocumentSymbol[] documentSymbols)
        {
            sb.AppendLine("Document Symbols:\n");
            FormatDocumentSymbols(sb, documentSymbols, 0);
            return sb.ToString();
        }

        if (symbols is SymbolInformation[] symbolInformation)
        {
            sb.AppendLine($"Found {symbolInformation.Length} symbol(s):\n");
            foreach (var sym in symbolInformation)
            {
                sb.AppendLine($"• {sym.Name} ({sym.Kind})");
                if (!string.IsNullOrEmpty(sym.ContainerName))
                    sb.AppendLine($"  Container: {sym.ContainerName}");
                sb.AppendLine($"  Line {sym.Location.Range.Start.Line + 1}");
            }
        }

        return sb.ToString();
    }

    public async Task<string> GetCodeActionsAsync(
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
            return "No code actions available.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {actions.Length} code action(s):\n");

        foreach (var action in actions)
        {
            sb.AppendLine($"• {action.Title}");
            if (!string.IsNullOrEmpty(action.Kind))
                sb.AppendLine($"  Kind: {action.Kind}");
        }

        return sb.ToString();
    }

    public async Task<string> RenameAsync(
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
            return "Cannot rename symbol at this position.";

        var sb = new StringBuilder();
        sb.AppendLine($"Rename to '{newName}' would affect:\n");

        var totalEdits = 0;
        foreach (var (uri, edits) in edit.Changes)
        {
            var path = new Uri(uri).LocalPath;
            sb.AppendLine($"• {path}: {edits.Length} edit(s)");
            totalEdits += edits.Length;
        }

        sb.AppendLine($"\nTotal: {totalEdits} edit(s) in {edit.Changes.Count} file(s)");
        sb.AppendLine("\nNote: This is a preview. Apply the rename in your editor to make changes.");

        return sb.ToString();
    }

    private static string FormatHoverContent(object contents)
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

    private static void FormatDocumentSymbols(StringBuilder sb, DocumentSymbol[] symbols, int indent)
    {
        var prefix = new string(' ', indent * 2);
        foreach (var sym in symbols)
        {
            sb.AppendLine($"{prefix}• {sym.Name} ({sym.Kind}) - Line {sym.Range.Start.Line + 1}");
            if (!string.IsNullOrEmpty(sym.Detail))
                sb.AppendLine($"{prefix}  {sym.Detail}");

            if (sym.Children != null && sym.Children.Length > 0)
                FormatDocumentSymbols(sb, sym.Children, indent + 1);
        }
    }
}
