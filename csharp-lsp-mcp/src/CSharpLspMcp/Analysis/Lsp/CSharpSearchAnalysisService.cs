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
        var symbols = await _lspClient.SearchWorkspaceSymbolsAsync(query, cancellationToken);
        if (symbols == null || symbols.Length == 0)
        {
            return new SearchSymbolsResponse(
                Summary: "No workspace symbols found.",
                Query: query,
                TotalMatches: 0,
                Symbols: Array.Empty<WorkspaceSymbolItem>(),
                TruncatedMatches: 0);
        }

        var effectiveMaxResults = Math.Max(1, maxResults);
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
            TruncatedMatches: Math.Max(0, symbols.Length - effectiveMaxResults));
    }

    public sealed record SearchSymbolsResponse(
        string Summary,
        string Query,
        int TotalMatches,
        WorkspaceSymbolItem[] Symbols,
        int TruncatedMatches) : IStructuredToolResult;

    public sealed record WorkspaceSymbolItem(
        string Name,
        string Kind,
        string? ContainerName,
        string FilePath,
        int Line,
        int Character);
}
