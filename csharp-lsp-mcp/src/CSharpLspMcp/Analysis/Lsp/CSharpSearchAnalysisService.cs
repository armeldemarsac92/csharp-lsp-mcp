using CSharpLspMcp.Contracts.Common;
using CSharpLspMcp.Lsp;

namespace CSharpLspMcp.Analysis.Lsp;

public sealed class CSharpSearchAnalysisService
{
    private readonly LspClient _lspClient;

    public CSharpSearchAnalysisService(LspClient lspClient)
    {
        _lspClient = lspClient;
    }

    public async Task<SearchSymbolsResponse> SearchSymbolsAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        var symbols = await SearchWorkspaceSymbolsWithFallbackAsync(query, cancellationToken);
        if (symbols == null || symbols.Length == 0)
        {
            return new SearchSymbolsResponse(
                Summary: "No workspace symbols found.",
                Query: query,
                TotalMatches: 0,
                Symbols: Array.Empty<WorkspaceSymbolItem>(),
                TruncatedMatches: 0,
                UsedSimpleNameFallback: false);
        }

        var effectiveMaxResults = Math.Max(1, maxResults);
        var usedSimpleNameFallback = UsedSimpleNameFallback(query, symbols);
        return new SearchSymbolsResponse(
            Summary: $"Found {symbols.Length} workspace symbol(s).",
            Query: query,
            TotalMatches: symbols.Length,
            Symbols: symbols.Take(effectiveMaxResults)
                .Select(symbol => new WorkspaceSymbolItem(
                    symbol.Name,
                    symbol.Kind.ToString(),
                    symbol.ContainerName,
                    new Uri(symbol.Location.Uri).LocalPath,
                    symbol.Location.Range.Start.Line + 1,
                    symbol.Location.Range.Start.Character + 1))
                .ToArray(),
            TruncatedMatches: Math.Max(0, symbols.Length - effectiveMaxResults),
            UsedSimpleNameFallback: usedSimpleNameFallback);
    }

    private async Task<SymbolInformation[]?> SearchWorkspaceSymbolsWithFallbackAsync(string query, CancellationToken cancellationToken)
    {
        var primaryMatches = await _lspClient.SearchWorkspaceSymbolsAsync(query, cancellationToken) ??
                             Array.Empty<SymbolInformation>();
        var simpleName = GetTrailingIdentifier(query);
        if (string.Equals(simpleName, query.Trim(), StringComparison.Ordinal))
            return primaryMatches;

        var simpleMatches = await _lspClient.SearchWorkspaceSymbolsAsync(simpleName, cancellationToken) ??
                            Array.Empty<SymbolInformation>();
        if (simpleMatches.Length == 0)
            return primaryMatches;

        return primaryMatches
            .Concat(simpleMatches)
            .DistinctBy(symbol => $"{symbol.ContainerName}|{symbol.Name}|{symbol.Location.Uri}|{symbol.Location.Range.Start.Line}|{symbol.Location.Range.Start.Character}")
            .ToArray();
    }

    private static bool UsedSimpleNameFallback(string query, IReadOnlyCollection<SymbolInformation> symbols)
    {
        var simpleName = GetTrailingIdentifier(query);
        return !string.Equals(simpleName, query.Trim(), StringComparison.Ordinal) &&
               symbols.Any(symbol => string.Equals(symbol.Name, simpleName, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetTrailingIdentifier(string query)
    {
        var trimmedQuery = query.Trim();
        var lastSeparator = trimmedQuery.LastIndexOf('.');
        return lastSeparator >= 0 && lastSeparator < trimmedQuery.Length - 1
            ? trimmedQuery[(lastSeparator + 1)..]
            : trimmedQuery;
    }

    public sealed record SearchSymbolsResponse(
        string Summary,
        string Query,
        int TotalMatches,
        WorkspaceSymbolItem[] Symbols,
        int TruncatedMatches,
        bool UsedSimpleNameFallback) : IStructuredToolResult;

    public sealed record WorkspaceSymbolItem(
        string Name,
        string Kind,
        string? ContainerName,
        string FilePath,
        int Line,
        int Character);
}
