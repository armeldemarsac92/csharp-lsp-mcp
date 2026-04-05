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

        var rankedSymbols = RankAndFilterSymbols(query, symbols);
        var effectiveMaxResults = Math.Max(1, maxResults);
        var usedSimpleNameFallback = UsedSimpleNameFallback(query, rankedSymbols);
        return new SearchSymbolsResponse(
            Summary: $"Found {rankedSymbols.Length} workspace symbol(s).",
            Query: query,
            TotalMatches: rankedSymbols.Length,
            Symbols: rankedSymbols.Take(effectiveMaxResults)
                .Select(symbol => new WorkspaceSymbolItem(
                    symbol.Name,
                    symbol.Kind.ToString(),
                    symbol.ContainerName,
                    new Uri(symbol.Location.Uri).LocalPath,
                    symbol.Location.Range.Start.Line + 1,
                    symbol.Location.Range.Start.Character + 1))
                .ToArray(),
            TruncatedMatches: Math.Max(0, rankedSymbols.Length - effectiveMaxResults),
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

    private static SymbolInformation[] RankAndFilterSymbols(
        string query,
        IReadOnlyCollection<SymbolInformation> symbols)
    {
        var rankedSymbols = symbols
            .Select(symbol => new RankedSymbol(
                Symbol: symbol,
                Score: ScoreSymbolMatch(symbol, query),
                IsTestSymbol: IsTestPath(new Uri(symbol.Location.Uri).LocalPath)))
            .OrderByDescending(symbol => symbol.Score)
            .ThenBy(symbol => symbol.IsTestSymbol)
            .ThenBy(symbol => symbol.Symbol.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(symbol => symbol.Symbol.ContainerName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(symbol => symbol.Symbol)
            .ToArray();

        if (!IsQualifiedQuery(query))
            return rankedSymbols;

        var strongQualifiedMatches = rankedSymbols
            .Where(symbol => IsStrongQualifiedMatch(symbol, query))
            .ToArray();
        return strongQualifiedMatches.Length > 0
            ? strongQualifiedMatches
            : rankedSymbols;
    }

    private static int ScoreSymbolMatch(SymbolInformation symbol, string query)
    {
        var trimmedQuery = query.Trim();
        if (string.IsNullOrWhiteSpace(trimmedQuery))
            return 0;

        var simpleName = GetTrailingIdentifier(trimmedQuery);
        var qualifiedName = string.IsNullOrWhiteSpace(symbol.ContainerName)
            ? symbol.Name
            : $"{symbol.ContainerName}.{symbol.Name}";
        var score = 0;

        if (string.Equals(qualifiedName, trimmedQuery, StringComparison.Ordinal))
            score += 1000;
        else if (string.Equals(qualifiedName, trimmedQuery, StringComparison.OrdinalIgnoreCase))
            score += 950;
        else if (qualifiedName.EndsWith(trimmedQuery, StringComparison.OrdinalIgnoreCase))
            score += 700;

        if (string.Equals(symbol.Name, trimmedQuery, StringComparison.Ordinal))
            score += 700;
        else if (string.Equals(symbol.Name, trimmedQuery, StringComparison.OrdinalIgnoreCase))
            score += 650;
        else if (symbol.Name.StartsWith(trimmedQuery, StringComparison.OrdinalIgnoreCase))
            score += 450;
        else if (symbol.Name.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase))
            score += 250;

        if (!string.Equals(simpleName, trimmedQuery, StringComparison.Ordinal))
        {
            if (string.Equals(symbol.Name, simpleName, StringComparison.Ordinal))
                score += 600;
            else if (string.Equals(symbol.Name, simpleName, StringComparison.OrdinalIgnoreCase))
                score += 550;
        }

        var namespacePrefix = GetContainerPrefix(trimmedQuery);
        if (!string.IsNullOrWhiteSpace(namespacePrefix) &&
            !string.IsNullOrWhiteSpace(symbol.ContainerName))
        {
            if (string.Equals(symbol.ContainerName, namespacePrefix, StringComparison.Ordinal))
                score += 500;
            else if (string.Equals(symbol.ContainerName, namespacePrefix, StringComparison.OrdinalIgnoreCase))
                score += 475;
            else if (symbol.ContainerName.EndsWith(namespacePrefix, StringComparison.OrdinalIgnoreCase))
                score += 300;
        }

        if (PathMatchesQualifiedQuery(symbol, trimmedQuery))
            score += 400;

        if (IsTestPath(new Uri(symbol.Location.Uri).LocalPath))
            score -= 100;

        return score;
    }

    private static bool IsStrongQualifiedMatch(SymbolInformation symbol, string query)
    {
        var trimmedQuery = query.Trim();
        var simpleName = GetTrailingIdentifier(trimmedQuery);
        if (!string.Equals(symbol.Name, simpleName, StringComparison.OrdinalIgnoreCase))
            return false;

        var containerPrefix = GetContainerPrefix(trimmedQuery);
        if (string.IsNullOrWhiteSpace(containerPrefix))
            return false;

        var qualifiedName = string.IsNullOrWhiteSpace(symbol.ContainerName)
            ? symbol.Name
            : $"{symbol.ContainerName}.{symbol.Name}";
        return string.Equals(qualifiedName, trimmedQuery, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(symbol.ContainerName, containerPrefix, StringComparison.OrdinalIgnoreCase) ||
               qualifiedName.EndsWith(trimmedQuery, StringComparison.OrdinalIgnoreCase) ||
               PathMatchesQualifiedQuery(symbol, trimmedQuery);
    }

    private static bool IsQualifiedQuery(string query)
        => query.Trim().Contains('.', StringComparison.Ordinal);

    private static string? GetContainerPrefix(string query)
    {
        var trimmedQuery = query.Trim();
        var lastSeparator = trimmedQuery.LastIndexOf('.');
        return lastSeparator > 0
            ? trimmedQuery[..lastSeparator]
            : null;
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

    private static bool PathMatchesQualifiedQuery(SymbolInformation symbol, string query)
    {
        var simpleName = GetTrailingIdentifier(query);
        var containerPrefix = GetContainerPrefix(query);
        if (string.IsNullOrWhiteSpace(simpleName) || string.IsNullOrWhiteSpace(containerPrefix))
            return false;

        var normalizedPath = new Uri(symbol.Location.Uri).LocalPath.Replace('\\', '/');
        return GetPathCandidates(containerPrefix, simpleName)
            .Any(candidate => normalizedPath.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> GetPathCandidates(string containerPrefix, string simpleName)
    {
        foreach (var candidatePrefix in ExpandContainerPathCandidates(containerPrefix))
        {
            yield return $"{candidatePrefix}/{simpleName}.cs";
            yield return $"{candidatePrefix}/{simpleName}";
        }
    }

    private static IEnumerable<string> ExpandContainerPathCandidates(string containerPrefix)
    {
        var segments = containerPrefix
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
            yield break;

        var separatorCombinations = 1 << (segments.Length - 1);
        for (var mask = 0; mask < separatorCombinations; mask++)
        {
            var path = segments[0];
            for (var index = 1; index < segments.Length; index++)
            {
                var useSlash = (mask & (1 << (index - 1))) != 0;
                path += useSlash ? "/" : ".";
                path += segments[index];
            }

            yield return path;
        }
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

    private sealed record RankedSymbol(
        SymbolInformation Symbol,
        int Score,
        bool IsTestSymbol);
}
