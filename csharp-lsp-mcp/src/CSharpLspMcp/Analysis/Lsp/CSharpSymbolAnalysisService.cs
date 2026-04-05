using System.Text;
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

    public async Task<string> AnalyzeSymbolAsync(
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
            return "Error: Provide either symbolQuery or filePath with line and character.";
        }

        var resolvedSymbol = hasPosition
            ? await ResolveSymbolFromPositionAsync(filePath!, line, character, content, cancellationToken)
            : await ResolveSymbolFromWorkspaceQueryAsync(symbolQuery!, cancellationToken);

        if (resolvedSymbol == null)
        {
            return hasPosition
                ? "No symbol could be resolved at the requested position."
                : $"No workspace symbol matched '{symbolQuery}'.";
        }

        return await BuildAnalysisAsync(resolvedSymbol, effectiveMaxResults, cancellationToken);
    }

    private async Task<string> BuildAnalysisAsync(
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

        var sb = new StringBuilder();
        sb.AppendLine($"Symbol: {resolvedSymbol.Name}{FormatKindSuffix(resolvedSymbol.Kind)}");

        if (!string.IsNullOrWhiteSpace(resolvedSymbol.ContainerName))
            sb.AppendLine($"Container: {resolvedSymbol.ContainerName}");

        if (!string.IsNullOrWhiteSpace(resolvedSymbol.Detail))
            sb.AppendLine($"Detail: {resolvedSymbol.Detail}");

        sb.AppendLine($"Location: {FormatPath(resolvedSymbol.FilePath)}:{resolvedSymbol.Line + 1}:{resolvedSymbol.Character + 1}");

        if (!string.IsNullOrWhiteSpace(resolvedSymbol.ResolutionNote))
            sb.AppendLine($"Note: {resolvedSymbol.ResolutionNote}");

        if (hover != null)
        {
            sb.AppendLine();
            sb.AppendLine("Hover:");
            sb.AppendLine(CSharpDocumentAnalysisService.FormatHoverContent(hover.Contents).TrimEnd());
        }

        AppendLocationsSection(sb, "Definitions", definitions, maxResults);
        AppendReferencesSection(sb, references, maxResults);
        AppendLocationsSection(sb, "Related Tests", relatedTests, maxResults);
        AppendImplementationsSection(sb, implementations, maxResults);
        AppendCallHierarchySection(sb, callHierarchy, maxResults);
        AppendTypeHierarchySection(sb, typeHierarchy, maxResults);

        return sb.ToString().TrimEnd();
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
                null);
        }

        return new ResolvedSymbol(
            Path.GetFileNameWithoutExtension(absolutePath),
            null,
            null,
            null,
            absolutePath,
            line,
            character,
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
            return new CallHierarchySummary(item, incoming, outgoing);
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

    private void AppendReferencesSection(StringBuilder sb, IReadOnlyCollection<Location> references, int maxResults)
    {
        sb.AppendLine();
        sb.AppendLine($"References ({references.Count}):");

        if (references.Count == 0)
        {
            sb.AppendLine("None.");
            return;
        }

        var shownReferences = references.Take(maxResults).ToArray();
        foreach (var group in shownReferences.GroupBy(location => new Uri(location.Uri).LocalPath))
        {
            sb.AppendLine($"• {FormatPath(group.Key)} ({group.Count()})");
            foreach (var location in group.OrderBy(location => location.Range.Start.Line))
                sb.AppendLine($"  Line {location.Range.Start.Line + 1}, Col {location.Range.Start.Character + 1}");
        }

        if (references.Count > shownReferences.Length)
            sb.AppendLine($"... and {references.Count - shownReferences.Length} more");
    }

    private void AppendLocationsSection(
        StringBuilder sb,
        string title,
        IReadOnlyCollection<Location> locations,
        int maxResults)
    {
        sb.AppendLine();
        sb.AppendLine($"{title} ({locations.Count}):");

        if (locations.Count == 0)
        {
            sb.AppendLine("None.");
            return;
        }

        foreach (var location in locations.Take(maxResults))
        {
            var path = new Uri(location.Uri).LocalPath;
            sb.AppendLine($"• {FormatPath(path)}:{location.Range.Start.Line + 1}:{location.Range.Start.Character + 1}");
        }

        if (locations.Count > maxResults)
            sb.AppendLine($"... and {locations.Count - maxResults} more");
    }

    private void AppendImplementationsSection(
        StringBuilder sb,
        ImplementationSummary implementations,
        int maxResults)
    {
        var totalCount = implementations.Locations.Count + implementations.TypeItems.Count;
        sb.AppendLine();
        sb.AppendLine($"Implementations ({totalCount}):");

        if (totalCount == 0)
        {
            sb.AppendLine("None.");
            return;
        }

        if (implementations.TypeItems.Count > 0)
        {
            foreach (var item in implementations.TypeItems.Take(maxResults))
            {
                var path = new Uri(item.Uri).LocalPath;
                sb.AppendLine($"• {item.Name} ({item.Kind})");
                if (!string.IsNullOrWhiteSpace(item.Detail))
                    sb.AppendLine($"  {item.Detail}");
                sb.AppendLine($"  {FormatPath(path)}:{item.SelectionRange.Start.Line + 1}");
            }

            if (implementations.TypeItems.Count > maxResults)
                sb.AppendLine($"... and {implementations.TypeItems.Count - maxResults} more");

            return;
        }

        foreach (var location in implementations.Locations.Take(maxResults))
        {
            var path = new Uri(location.Uri).LocalPath;
            sb.AppendLine($"• {FormatPath(path)}:{location.Range.Start.Line + 1}:{location.Range.Start.Character + 1}");
        }

        if (implementations.Locations.Count > maxResults)
            sb.AppendLine($"... and {implementations.Locations.Count - maxResults} more");
    }

    private void AppendCallHierarchySection(
        StringBuilder sb,
        CallHierarchySummary callHierarchy,
        int maxResults)
    {
        var incomingCount = callHierarchy.Incoming.Count;
        var outgoingCount = callHierarchy.Outgoing.Count;

        sb.AppendLine();
        sb.AppendLine($"Incoming Calls ({incomingCount}):");
        if (incomingCount == 0)
        {
            sb.AppendLine("None.");
        }
        else
        {
            foreach (var call in callHierarchy.Incoming.Take(maxResults))
                AppendCallSite(sb, call.From, call.FromRanges.FirstOrDefault()?.Start);

            if (incomingCount > maxResults)
                sb.AppendLine($"... and {incomingCount - maxResults} more");
        }

        sb.AppendLine();
        sb.AppendLine($"Outgoing Calls ({outgoingCount}):");
        if (outgoingCount == 0)
        {
            sb.AppendLine("None.");
            return;
        }

        foreach (var call in callHierarchy.Outgoing.Take(maxResults))
            AppendCallSite(sb, call.To, call.FromRanges.FirstOrDefault()?.Start);

        if (outgoingCount > maxResults)
            sb.AppendLine($"... and {outgoingCount - maxResults} more");
    }

    private void AppendTypeHierarchySection(
        StringBuilder sb,
        TypeHierarchySummary typeHierarchy,
        int maxResults)
    {
        sb.AppendLine();
        sb.AppendLine($"Supertypes ({typeHierarchy.Supertypes.Count}):");
        AppendTypeHierarchyItems(sb, typeHierarchy.Supertypes, maxResults);

        sb.AppendLine();
        sb.AppendLine($"Subtypes ({typeHierarchy.Subtypes.Count}):");
        AppendTypeHierarchyItems(sb, typeHierarchy.Subtypes, maxResults);
    }

    private void AppendCallSite(StringBuilder sb, CallHierarchyItem item, Position? position)
    {
        var path = new Uri(item.Uri).LocalPath;
        sb.AppendLine($"• {item.Name} ({item.Kind})");
        if (!string.IsNullOrWhiteSpace(item.Detail))
            sb.AppendLine($"  {item.Detail}");

        var location = position ?? item.SelectionRange.Start;
        sb.AppendLine($"  {FormatPath(path)}:{location.Line + 1}:{location.Character + 1}");
    }

    private void AppendTypeHierarchyItems(
        StringBuilder sb,
        IReadOnlyCollection<TypeHierarchyItem> items,
        int maxResults)
    {
        if (items.Count == 0)
        {
            sb.AppendLine("None.");
            return;
        }

        foreach (var item in items.Take(maxResults))
        {
            var path = new Uri(item.Uri).LocalPath;
            sb.AppendLine($"• {item.Name} ({item.Kind})");
            if (!string.IsNullOrWhiteSpace(item.Detail))
                sb.AppendLine($"  {item.Detail}");
            sb.AppendLine($"  {FormatPath(path)}:{item.SelectionRange.Start.Line + 1}");
        }

        if (items.Count > maxResults)
            sb.AppendLine($"... and {items.Count - maxResults} more");
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
        IReadOnlyCollection<CallHierarchyOutgoingCall> Outgoing)
    {
        public static CallHierarchySummary Empty { get; } =
            new(null, Array.Empty<CallHierarchyIncomingCall>(), Array.Empty<CallHierarchyOutgoingCall>());
    }

    private sealed record TypeHierarchySummary(
        TypeHierarchyItem? Root,
        IReadOnlyCollection<TypeHierarchyItem> Supertypes,
        IReadOnlyCollection<TypeHierarchyItem> Subtypes)
    {
        public static TypeHierarchySummary Empty { get; } =
            new(null, Array.Empty<TypeHierarchyItem>(), Array.Empty<TypeHierarchyItem>());
    }
}
