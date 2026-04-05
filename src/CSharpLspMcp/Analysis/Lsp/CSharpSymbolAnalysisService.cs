using CSharpLspMcp.Contracts.Common;
using CSharpLspMcp.Lsp;
using CSharpLspMcp.Workspace;

namespace CSharpLspMcp.Analysis.Lsp;

public sealed class CSharpSymbolAnalysisService
{
    private readonly LspClient _lspClient;
    private readonly CSharpWorkspaceSession _workspaceSession;

    public CSharpSymbolAnalysisService(
        LspClient lspClient,
        CSharpWorkspaceSession workspaceSession)
    {
        _lspClient = lspClient;
        _workspaceSession = workspaceSession;
    }

    public async Task<SymbolAnalysisResponse> AnalyzeSymbolAsync(
        string? symbolQuery,
        string? filePath,
        int line,
        int character,
        string? content,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var effectiveMaxResults = Math.Max(1, maxResults);
        var hasQuery = !string.IsNullOrWhiteSpace(symbolQuery);
        var hasPosition = !string.IsNullOrWhiteSpace(filePath) && line >= 0 && character >= 0;

        if (!hasQuery && !hasPosition)
        {
            throw new InvalidOperationException("Provide either symbolQuery or filePath with line and character.");
        }

        var resolvedSymbol = hasPosition
            ? await ResolveSymbolFromPositionAsync(filePath!, line, character, content, cancellationToken)
            : await ResolveSymbolFromWorkspaceQueryAsync(symbolQuery!, cancellationToken);

        if (resolvedSymbol == null)
        {
            return hasPosition
                ? new SymbolAnalysisResponse(
                    Summary: "No symbol could be resolved at the requested position.",
                    Symbol: null,
                    HoverText: null,
                    Definitions: Array.Empty<LocationItem>(),
                    TruncatedDefinitions: 0,
                    References: Array.Empty<LocationItem>(),
                    TruncatedReferences: 0,
                    RelatedTests: Array.Empty<LocationItem>(),
                    TruncatedRelatedTests: 0,
                    Implementations: Array.Empty<HierarchyNodeItem>(),
                    TruncatedImplementations: 0,
                    IncomingCalls: Array.Empty<CallSiteItem>(),
                    TruncatedIncomingCalls: 0,
                    OutgoingCalls: Array.Empty<CallSiteItem>(),
                    TruncatedOutgoingCalls: 0,
                    Supertypes: Array.Empty<HierarchyNodeItem>(),
                    TruncatedSupertypes: 0,
                    Subtypes: Array.Empty<HierarchyNodeItem>(),
                    TruncatedSubtypes: 0,
                    UsedHeuristicOutgoingFallback: false)
                : new SymbolAnalysisResponse(
                    Summary: $"No workspace symbol matched '{symbolQuery}'.",
                    Symbol: null,
                    HoverText: null,
                    Definitions: Array.Empty<LocationItem>(),
                    TruncatedDefinitions: 0,
                    References: Array.Empty<LocationItem>(),
                    TruncatedReferences: 0,
                    RelatedTests: Array.Empty<LocationItem>(),
                    TruncatedRelatedTests: 0,
                    Implementations: Array.Empty<HierarchyNodeItem>(),
                    TruncatedImplementations: 0,
                    IncomingCalls: Array.Empty<CallSiteItem>(),
                    TruncatedIncomingCalls: 0,
                    OutgoingCalls: Array.Empty<CallSiteItem>(),
                    TruncatedOutgoingCalls: 0,
                    Supertypes: Array.Empty<HierarchyNodeItem>(),
                    TruncatedSupertypes: 0,
                    Subtypes: Array.Empty<HierarchyNodeItem>(),
                    TruncatedSubtypes: 0,
                    UsedHeuristicOutgoingFallback: false);
        }

        return await BuildAnalysisAsync(resolvedSymbol, effectiveMaxResults, cancellationToken);
    }

    private async Task<SymbolAnalysisResponse> BuildAnalysisAsync(
        ResolvedSymbol resolvedSymbol,
        int maxResults,
        CancellationToken cancellationToken)
    {
        await _workspaceSession.EnsureDocumentOpenAsync(resolvedSymbol.FilePath, null, cancellationToken);

        var hover = await _lspClient.GetHoverAsync(
            resolvedSymbol.FilePath,
            resolvedSymbol.Line,
            resolvedSymbol.Character,
            cancellationToken);
        var definitions = await _lspClient.GetDefinitionAsync(
                resolvedSymbol.FilePath,
                resolvedSymbol.Line,
                resolvedSymbol.Character,
                cancellationToken) ??
            Array.Empty<Location>();
        var references = await _lspClient.GetReferencesAsync(
                resolvedSymbol.FilePath,
                resolvedSymbol.Line,
                resolvedSymbol.Character,
                true,
                cancellationToken) ??
            Array.Empty<Location>();
        var implementations = await GetImplementationsAsync(resolvedSymbol, cancellationToken);
        var callHierarchy = await GetCallHierarchyAsync(resolvedSymbol, cancellationToken);
        var typeHierarchy = await GetTypeHierarchyAsync(resolvedSymbol, cancellationToken);
        var relatedTests = references
            .Where(location => IsTestPath(new Uri(location.Uri).LocalPath))
            .ToArray();

        return new SymbolAnalysisResponse(
            Summary: BuildAnalysisSummary(resolvedSymbol, references.Length, implementations.Locations.Count + implementations.TypeItems.Count, relatedTests.Length, callHierarchy.UsedHeuristicOutgoingFallback),
            Symbol: new SymbolIdentity(
                resolvedSymbol.Name,
                resolvedSymbol.Kind?.ToString(),
                resolvedSymbol.Detail,
                resolvedSymbol.ContainerName,
                FormatPath(resolvedSymbol.FilePath),
                resolvedSymbol.Line + 1,
                resolvedSymbol.Character + 1,
                resolvedSymbol.ResolutionNote),
            HoverText: hover == null ? null : CSharpDocumentAnalysisService.FormatHoverContent(hover.Contents).TrimEnd(),
            Definitions: definitions.Take(maxResults).Select(MapLocation).ToArray(),
            TruncatedDefinitions: Math.Max(0, definitions.Length - maxResults),
            References: references.Take(maxResults).Select(MapLocation).ToArray(),
            TruncatedReferences: Math.Max(0, references.Length - maxResults),
            RelatedTests: relatedTests.Take(maxResults).Select(MapLocation).ToArray(),
            TruncatedRelatedTests: Math.Max(0, relatedTests.Length - maxResults),
            Implementations: implementations.TypeItems.Count > 0
                ? implementations.TypeItems.Take(maxResults).Select(MapTypeHierarchyItem).ToArray()
                : implementations.Locations.Take(maxResults).Select(MapLocationAsHierarchyNode).ToArray(),
            TruncatedImplementations: implementations.TypeItems.Count > 0
                ? Math.Max(0, implementations.TypeItems.Count - maxResults)
                : Math.Max(0, implementations.Locations.Count - maxResults),
            IncomingCalls: callHierarchy.Incoming.Take(maxResults)
                .Select(call => MapCallSite(call.From, call.FromRanges.FirstOrDefault()?.Start))
                .ToArray(),
            TruncatedIncomingCalls: Math.Max(0, callHierarchy.Incoming.Count - maxResults),
            OutgoingCalls: callHierarchy.HeuristicOutgoing.Count > 0
                ? callHierarchy.HeuristicOutgoing.Take(maxResults).ToArray()
                : callHierarchy.Outgoing.Take(maxResults)
                    .Select(call => MapCallSite(call.To, call.FromRanges.FirstOrDefault()?.Start))
                    .ToArray(),
            TruncatedOutgoingCalls: callHierarchy.HeuristicOutgoing.Count > 0
                ? Math.Max(0, callHierarchy.HeuristicOutgoing.Count - maxResults)
                : Math.Max(0, callHierarchy.Outgoing.Count - maxResults),
            Supertypes: typeHierarchy.Supertypes.Take(maxResults).Select(MapTypeHierarchyItem).ToArray(),
            TruncatedSupertypes: Math.Max(0, typeHierarchy.Supertypes.Count - maxResults),
            Subtypes: typeHierarchy.Subtypes.Take(maxResults).Select(MapTypeHierarchyItem).ToArray(),
            TruncatedSubtypes: Math.Max(0, typeHierarchy.Subtypes.Count - maxResults),
            UsedHeuristicOutgoingFallback: callHierarchy.UsedHeuristicOutgoingFallback);
    }

    private async Task<ResolvedSymbol?> ResolveSymbolFromPositionAsync(
        string filePath,
        int line,
        int character,
        string? content,
        CancellationToken cancellationToken)
    {
        var absolutePath = _workspaceSession.GetAbsolutePath(filePath);
        await _workspaceSession.EnsureDocumentOpenAsync(absolutePath, content, cancellationToken);

        var documentSymbol = await TryResolveDocumentSymbolAsync(absolutePath, line, character, cancellationToken);
        if (documentSymbol != null)
        {
            return new ResolvedSymbol(
                documentSymbol.Name,
                documentSymbol.Kind,
                documentSymbol.Detail,
                documentSymbol.ContainerName,
                absolutePath,
                documentSymbol.SelectionRange.Start.Line,
                documentSymbol.SelectionRange.Start.Character,
                documentSymbol.Range,
                null);
        }

        var definitions = await _lspClient.GetDefinitionAsync(absolutePath, line, character, cancellationToken);
        var definition = definitions?.FirstOrDefault();
        if (definition != null)
        {
            var definitionPath = new Uri(definition.Uri).LocalPath;
            await _workspaceSession.EnsureDocumentOpenAsync(definitionPath, null, cancellationToken);

            var definitionSymbol = await TryResolveDocumentSymbolAsync(
                definitionPath,
                definition.Range.Start.Line,
                definition.Range.Start.Character,
                cancellationToken);
            if (definitionSymbol != null)
            {
                return new ResolvedSymbol(
                    definitionSymbol.Name,
                    definitionSymbol.Kind,
                    definitionSymbol.Detail,
                    definitionSymbol.ContainerName,
                    definitionPath,
                    definitionSymbol.SelectionRange.Start.Line,
                    definitionSymbol.SelectionRange.Start.Character,
                    definitionSymbol.Range,
                    "No document symbol exactly matched the requested position; resolved the target symbol from definition.");
            }
        }

        return new ResolvedSymbol(
            Path.GetFileNameWithoutExtension(absolutePath),
            null,
            null,
            null,
            absolutePath,
            line,
            character,
            null,
            "No document symbol exactly matched the requested position; using the raw location.");
    }

    private async Task<ResolvedSymbol?> ResolveSymbolFromWorkspaceQueryAsync(
        string symbolQuery,
        CancellationToken cancellationToken)
    {
        var matches = await SearchWorkspaceSymbolsWithFallbackAsync(symbolQuery, cancellationToken);
        if (matches == null || matches.Length == 0)
            return null;

        var selectedSymbol = SelectBestWorkspaceSymbolMatch(matches, symbolQuery);
        var absolutePath = new Uri(selectedSymbol.Location.Uri).LocalPath;
        await _workspaceSession.EnsureDocumentOpenAsync(absolutePath, null, cancellationToken);

        var documentSymbol = await TryResolveDocumentSymbolAsync(
            absolutePath,
            selectedSymbol.Location.Range.Start.Line,
            selectedSymbol.Location.Range.Start.Character,
            cancellationToken);
        var resolutionNote = BuildWorkspaceMatchNote(matches, selectedSymbol, symbolQuery);

        if (documentSymbol != null)
        {
            return new ResolvedSymbol(
                documentSymbol.Name,
                documentSymbol.Kind,
                documentSymbol.Detail,
                documentSymbol.ContainerName ?? selectedSymbol.ContainerName,
                absolutePath,
                documentSymbol.SelectionRange.Start.Line,
                documentSymbol.SelectionRange.Start.Character,
                documentSymbol.Range,
                resolutionNote);
        }

        return new ResolvedSymbol(
            selectedSymbol.Name,
            selectedSymbol.Kind,
            null,
            selectedSymbol.ContainerName,
            absolutePath,
            selectedSymbol.Location.Range.Start.Line,
            selectedSymbol.Location.Range.Start.Character,
            selectedSymbol.Location.Range,
            resolutionNote);
    }

    private async Task<DocumentSymbolSelection?> TryResolveDocumentSymbolAsync(
        string absolutePath,
        int line,
        int character,
        CancellationToken cancellationToken)
    {
        var symbols = await _lspClient.GetDocumentSymbolsAsync(absolutePath, cancellationToken);
        return symbols switch
        {
            DocumentSymbol[] documentSymbols => TryFindDeepestDocumentSymbol(documentSymbols, line, character),
            SymbolInformation[] symbolInformation => TryFindBestSymbolInformation(symbolInformation, absolutePath, line, character),
            _ => null
        };
    }

    private async Task<ImplementationSummary> GetImplementationsAsync(
        ResolvedSymbol resolvedSymbol,
        CancellationToken cancellationToken)
    {
        try
        {
            var hierarchyItems = await _lspClient.PrepareTypeHierarchyAsync(
                resolvedSymbol.FilePath,
                resolvedSymbol.Line,
                resolvedSymbol.Character,
                cancellationToken);
            var hierarchyRoot = hierarchyItems?.FirstOrDefault();
            if (hierarchyRoot != null && SupportsTypeHierarchyImplementations(hierarchyRoot.Kind))
            {
                var subtypeItems = await _lspClient.GetTypeHierarchySubtypesAsync(hierarchyRoot, cancellationToken) ??
                                   Array.Empty<TypeHierarchyItem>();
                return new ImplementationSummary(Array.Empty<Location>(), subtypeItems);
            }
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            var locations = await _lspClient.GetImplementationsAsync(
                                resolvedSymbol.FilePath,
                                resolvedSymbol.Line,
                                resolvedSymbol.Character,
                                cancellationToken) ??
                            Array.Empty<Location>();
            return new ImplementationSummary(locations, Array.Empty<TypeHierarchyItem>());
        }
        catch (InvalidOperationException)
        {
            return new ImplementationSummary(Array.Empty<Location>(), Array.Empty<TypeHierarchyItem>());
        }
    }

    private async Task<CallHierarchySummary> GetCallHierarchyAsync(
        ResolvedSymbol resolvedSymbol,
        CancellationToken cancellationToken)
    {
        try
        {
            var items = await _lspClient.PrepareCallHierarchyAsync(
                resolvedSymbol.FilePath,
                resolvedSymbol.Line,
                resolvedSymbol.Character,
                cancellationToken);
            var item = items?.FirstOrDefault();
            if (item == null)
                return CallHierarchySummary.Empty;

            var incoming = await _lspClient.GetIncomingCallsAsync(item, cancellationToken) ??
                           Array.Empty<CallHierarchyIncomingCall>();
            var outgoing = await _lspClient.GetOutgoingCallsAsync(item, cancellationToken) ??
                           Array.Empty<CallHierarchyOutgoingCall>();
            var fallbackRange = resolvedSymbol.ContainingRange;
            if (fallbackRange == null)
            {
                var documentSymbols = await _lspClient.GetDocumentSymbolsAsync(resolvedSymbol.FilePath, cancellationToken);
                fallbackRange = CSharpSourceHeuristics.FindContainingSymbolRange(
                    documentSymbols,
                    resolvedSymbol.Line,
                    resolvedSymbol.Character);
            }

            var heuristicOutgoing = outgoing.Length == 0
                ? await CSharpSourceHeuristics.ResolveOutgoingCallsAsync(
                    _lspClient,
                    resolvedSymbol.FilePath,
                    null,
                    fallbackRange ?? item.Range,
                    CSharpSourceHeuristics.GetInvocationAnchorName(resolvedSymbol.Name),
                    cancellationToken)
                : Array.Empty<CSharpSourceHeuristics.HeuristicOutgoingCall>();
            return new CallHierarchySummary(
                item,
                incoming,
                outgoing,
                heuristicOutgoing.Select(call => new CallSiteItem(
                    call.Name,
                    call.Kind,
                    call.Detail,
                    FormatPath(call.FilePath),
                    call.Line,
                    call.Character))
                    .ToArray());
        }
        catch (InvalidOperationException)
        {
            return CallHierarchySummary.Empty;
        }
    }

    private async Task<TypeHierarchySummary> GetTypeHierarchyAsync(
        ResolvedSymbol resolvedSymbol,
        CancellationToken cancellationToken)
    {
        try
        {
            var items = await _lspClient.PrepareTypeHierarchyAsync(
                resolvedSymbol.FilePath,
                resolvedSymbol.Line,
                resolvedSymbol.Character,
                cancellationToken);
            var item = items?.FirstOrDefault();
            if (item == null)
                return TypeHierarchySummary.Empty;

            var supertypes = await _lspClient.GetTypeHierarchySupertypesAsync(item, cancellationToken) ??
                             Array.Empty<TypeHierarchyItem>();
            var subtypes = await _lspClient.GetTypeHierarchySubtypesAsync(item, cancellationToken) ??
                           Array.Empty<TypeHierarchyItem>();
            return new TypeHierarchySummary(item, supertypes, subtypes);
        }
        catch (InvalidOperationException)
        {
            return TypeHierarchySummary.Empty;
        }
    }

    private LocationItem MapLocation(Location location)
    {
        var path = new Uri(location.Uri).LocalPath;
        return new LocationItem(
            FormatPath(path),
            location.Range.Start.Line + 1,
            location.Range.Start.Character + 1);
    }

    private HierarchyNodeItem MapTypeHierarchyItem(TypeHierarchyItem item)
    {
        var path = new Uri(item.Uri).LocalPath;
        return new HierarchyNodeItem(
            item.Name,
            item.Kind.ToString(),
            item.Detail,
            FormatPath(path),
            item.SelectionRange.Start.Line + 1,
            item.SelectionRange.Start.Character + 1);
    }

    private HierarchyNodeItem MapLocationAsHierarchyNode(Location location)
    {
        var path = new Uri(location.Uri).LocalPath;
        return new HierarchyNodeItem(
            Path.GetFileName(path),
            "Location",
            null,
            FormatPath(path),
            location.Range.Start.Line + 1,
            location.Range.Start.Character + 1);
    }

    private CallSiteItem MapCallSite(CallHierarchyItem item, Position? position)
    {
        var path = new Uri(item.Uri).LocalPath;
        var location = position ?? item.SelectionRange.Start;
        return new CallSiteItem(
            item.Name,
            item.Kind.ToString(),
            item.Detail,
            FormatPath(path),
            location.Line + 1,
            location.Character + 1);
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

    private static string FormatKindSuffix(SymbolKind? kind)
        => kind == null ? string.Empty : $" ({kind})";

    private static string BuildAnalysisSummary(
        ResolvedSymbol resolvedSymbol,
        int referenceCount,
        int implementationCount,
        int relatedTestCount,
        bool usedHeuristicOutgoingFallback)
    {
        var summary = $"Analyzed {resolvedSymbol.Name}{FormatKindSuffix(resolvedSymbol.Kind)} with {referenceCount} reference(s), {implementationCount} implementation(s), and {relatedTestCount} related test reference(s).";
        return usedHeuristicOutgoingFallback
            ? $"{summary} Outgoing calls were resolved with a definition-based heuristic fallback."
            : summary;
    }

    private static string? BuildWorkspaceMatchNote(
        IReadOnlyCollection<SymbolInformation> matches,
        SymbolInformation selectedSymbol,
        string symbolQuery)
    {
        if (matches.Count == 1)
            return $"Resolved workspace query '{symbolQuery}' to a single symbol match.";

        var exactNameMatches = matches.Count(match => string.Equals(match.Name, symbolQuery, StringComparison.OrdinalIgnoreCase));
        return exactNameMatches > 1
            ? $"Workspace query '{symbolQuery}' matched {matches.Count} symbols and {exactNameMatches} exact name matches; showing the highest-ranked match in {selectedSymbol.ContainerName ?? "(global)" }."
            : $"Workspace query '{symbolQuery}' matched {matches.Count} symbols; showing the highest-ranked match.";
    }

    private static SymbolInformation SelectBestWorkspaceSymbolMatch(
        IReadOnlyCollection<SymbolInformation> symbols,
        string query)
    {
        return symbols
            .OrderByDescending(symbol => ScoreSymbolMatch(symbol, query))
            .ThenBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private static int ScoreSymbolMatch(SymbolInformation symbol, string query)
    {
        var trimmedQuery = query.Trim();
        if (string.IsNullOrWhiteSpace(trimmedQuery))
            return 0;

        var simpleName = GetTrailingIdentifier(trimmedQuery);
        var score = 0;
        if (string.Equals(symbol.Name, trimmedQuery, StringComparison.Ordinal))
            score += 500;
        else if (string.Equals(symbol.Name, trimmedQuery, StringComparison.OrdinalIgnoreCase))
            score += 450;
        else if (symbol.Name.StartsWith(trimmedQuery, StringComparison.Ordinal))
            score += 400;
        else if (symbol.Name.StartsWith(trimmedQuery, StringComparison.OrdinalIgnoreCase))
            score += 350;
        else if (symbol.Name.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase))
            score += 250;

        if (!string.Equals(simpleName, trimmedQuery, StringComparison.Ordinal))
        {
            if (string.Equals(symbol.Name, simpleName, StringComparison.Ordinal))
                score += 475;
            else if (string.Equals(symbol.Name, simpleName, StringComparison.OrdinalIgnoreCase))
                score += 425;
            else if (symbol.Name.Contains(simpleName, StringComparison.OrdinalIgnoreCase))
                score += 225;
        }

        var qualifiedName = string.IsNullOrWhiteSpace(symbol.ContainerName)
            ? symbol.Name
            : $"{symbol.ContainerName}.{symbol.Name}";

        if (string.Equals(qualifiedName, trimmedQuery, StringComparison.Ordinal))
            score += 600;
        else if (string.Equals(qualifiedName, trimmedQuery, StringComparison.OrdinalIgnoreCase))
            score += 550;
        else if (qualifiedName.EndsWith(trimmedQuery, StringComparison.OrdinalIgnoreCase))
            score += 300;

        var namespacePrefix = GetContainerPrefix(trimmedQuery);
        if (!string.IsNullOrWhiteSpace(namespacePrefix) &&
            !string.IsNullOrWhiteSpace(symbol.ContainerName))
        {
            if (string.Equals(symbol.ContainerName, namespacePrefix, StringComparison.Ordinal))
                score += 350;
            else if (string.Equals(symbol.ContainerName, namespacePrefix, StringComparison.OrdinalIgnoreCase))
                score += 325;
            else if (symbol.ContainerName.EndsWith(namespacePrefix, StringComparison.OrdinalIgnoreCase))
                score += 200;
        }

        return score;
    }

    private static DocumentSymbolSelection? TryFindDeepestDocumentSymbol(
        IEnumerable<DocumentSymbol> symbols,
        int line,
        int character,
        string? containerName = null)
    {
        DocumentSymbolSelection? bestMatch = null;

        foreach (var symbol in symbols)
        {
            if (!ContainsPosition(symbol.Range, line, character))
                continue;

            var currentContainer = string.IsNullOrWhiteSpace(containerName)
                ? symbol.Name
                : $"{containerName}.{symbol.Name}";

            var childMatch = symbol.Children == null
                ? null
                : TryFindDeepestDocumentSymbol(symbol.Children, line, character, currentContainer);
            var currentMatch = childMatch ?? new DocumentSymbolSelection(
                symbol.Name,
                symbol.Kind,
                symbol.Detail,
                containerName,
                symbol.Range,
                symbol.SelectionRange);
            bestMatch = SelectMoreSpecificDocumentSymbol(bestMatch, currentMatch);
        }

        return bestMatch;
    }

    private static DocumentSymbolSelection? TryFindBestSymbolInformation(
        IEnumerable<SymbolInformation> symbols,
        string absolutePath,
        int line,
        int character)
    {
        return symbols
            .Where(symbol => string.Equals(new Uri(symbol.Location.Uri).LocalPath, absolutePath, StringComparison.OrdinalIgnoreCase))
            .Where(symbol => ContainsPosition(symbol.Location.Range, line, character))
            .OrderBy(symbol => GetRangeSpan(symbol.Location.Range))
            .Select(symbol => new DocumentSymbolSelection(
                symbol.Name,
                symbol.Kind,
                null,
                symbol.ContainerName,
                symbol.Location.Range,
                symbol.Location.Range))
            .FirstOrDefault();
    }

    private static bool ContainsPosition(CSharpLspMcp.Lsp.Range range, int line, int character)
    {
        if (line < range.Start.Line || line > range.End.Line)
            return false;

        if (line == range.Start.Line && character < range.Start.Character)
            return false;

        if (line == range.End.Line && character > range.End.Character)
            return false;

        return true;
    }

    private static int GetRangeSpan(CSharpLspMcp.Lsp.Range range)
        => ((range.End.Line - range.Start.Line) * 10_000) + (range.End.Character - range.Start.Character);

    private static DocumentSymbolSelection SelectMoreSpecificDocumentSymbol(
        DocumentSymbolSelection? current,
        DocumentSymbolSelection candidate)
    {
        if (current == null)
            return candidate;

        var currentSpan = GetRangeSpan(current.Range);
        var candidateSpan = GetRangeSpan(candidate.Range);
        if (candidateSpan < currentSpan)
            return candidate;

        if (candidateSpan == currentSpan && GetSymbolSpecificity(candidate.Kind) > GetSymbolSpecificity(current.Kind))
            return candidate;

        return current;
    }

    private static bool SupportsTypeHierarchyImplementations(SymbolKind kind)
        => kind is SymbolKind.Interface or SymbolKind.Class;

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

    private async Task<SymbolInformation[]?> SearchWorkspaceSymbolsWithFallbackAsync(
        string symbolQuery,
        CancellationToken cancellationToken)
    {
        var primaryMatches = await _lspClient.SearchWorkspaceSymbolsAsync(symbolQuery, cancellationToken) ??
                             Array.Empty<SymbolInformation>();
        var simpleName = GetTrailingIdentifier(symbolQuery);

        if (string.Equals(simpleName, symbolQuery.Trim(), StringComparison.Ordinal))
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

    private static string GetTrailingIdentifier(string query)
    {
        var trimmedQuery = query.Trim();
        var lastSeparator = trimmedQuery.LastIndexOf('.');
        return lastSeparator >= 0 && lastSeparator < trimmedQuery.Length - 1
            ? trimmedQuery[(lastSeparator + 1)..]
            : trimmedQuery;
    }

    private static string? GetContainerPrefix(string query)
    {
        var trimmedQuery = query.Trim();
        var lastSeparator = trimmedQuery.LastIndexOf('.');
        return lastSeparator > 0
            ? trimmedQuery[..lastSeparator]
            : null;
    }

    private static int GetSymbolSpecificity(SymbolKind kind)
        => kind switch
        {
            SymbolKind.Method => 6,
            SymbolKind.Property => 5,
            SymbolKind.Field => 5,
            SymbolKind.Constructor => 5,
            SymbolKind.Class => 4,
            SymbolKind.Struct => 4,
            SymbolKind.Interface => 4,
            SymbolKind.Namespace => 2,
            SymbolKind.File => 1,
            _ => 3
        };

    private sealed record ResolvedSymbol(
        string Name,
        SymbolKind? Kind,
        string? Detail,
        string? ContainerName,
        string FilePath,
        int Line,
        int Character,
        CSharpLspMcp.Lsp.Range? ContainingRange,
        string? ResolutionNote);

    private sealed record DocumentSymbolSelection(
        string Name,
        SymbolKind Kind,
        string? Detail,
        string? ContainerName,
        CSharpLspMcp.Lsp.Range Range,
        CSharpLspMcp.Lsp.Range SelectionRange);

    private sealed record ImplementationSummary(
        IReadOnlyCollection<Location> Locations,
        IReadOnlyCollection<TypeHierarchyItem> TypeItems);

    private sealed record CallHierarchySummary(
        CallHierarchyItem? Root,
        IReadOnlyCollection<CallHierarchyIncomingCall> Incoming,
        IReadOnlyCollection<CallHierarchyOutgoingCall> Outgoing,
        IReadOnlyCollection<CallSiteItem> HeuristicOutgoing)
    {
        public static CallHierarchySummary Empty { get; } =
            new(null, Array.Empty<CallHierarchyIncomingCall>(), Array.Empty<CallHierarchyOutgoingCall>(), Array.Empty<CallSiteItem>());

        public bool UsedHeuristicOutgoingFallback => HeuristicOutgoing.Count > 0;
    }

    private sealed record TypeHierarchySummary(
        TypeHierarchyItem? Root,
        IReadOnlyCollection<TypeHierarchyItem> Supertypes,
        IReadOnlyCollection<TypeHierarchyItem> Subtypes)
    {
        public static TypeHierarchySummary Empty { get; } =
            new(null, Array.Empty<TypeHierarchyItem>(), Array.Empty<TypeHierarchyItem>());
    }

    public sealed record SymbolAnalysisResponse(
        string Summary,
        SymbolIdentity? Symbol,
        string? HoverText,
        LocationItem[] Definitions,
        int TruncatedDefinitions,
        LocationItem[] References,
        int TruncatedReferences,
        LocationItem[] RelatedTests,
        int TruncatedRelatedTests,
        HierarchyNodeItem[] Implementations,
        int TruncatedImplementations,
        CallSiteItem[] IncomingCalls,
        int TruncatedIncomingCalls,
        CallSiteItem[] OutgoingCalls,
        int TruncatedOutgoingCalls,
        HierarchyNodeItem[] Supertypes,
        int TruncatedSupertypes,
        HierarchyNodeItem[] Subtypes,
        int TruncatedSubtypes,
        bool UsedHeuristicOutgoingFallback) : IStructuredToolResult;

    public sealed record SymbolIdentity(
        string Name,
        string? Kind,
        string? Detail,
        string? ContainerName,
        string FilePath,
        int Line,
        int Character,
        string? ResolutionNote);

    public sealed record LocationItem(
        string FilePath,
        int Line,
        int Character);

    public sealed record HierarchyNodeItem(
        string Name,
        string Kind,
        string? Detail,
        string FilePath,
        int Line,
        int Character);

    public sealed record CallSiteItem(
        string Name,
        string Kind,
        string? Detail,
        string FilePath,
        int Line,
        int Character);
}
