using CSharpLspMcp.Contracts.Common;
using CSharpLspMcp.Lsp;
using CSharpLspMcp.Workspace;

namespace CSharpLspMcp.Analysis.Lsp;

public sealed class CSharpHierarchyAnalysisService
{
    private readonly LspClient _lspClient;
    private readonly CSharpWorkspaceSession _workspaceSession;

    public CSharpHierarchyAnalysisService(
        LspClient lspClient,
        CSharpWorkspaceSession workspaceSession)
    {
        _lspClient = lspClient;
        _workspaceSession = workspaceSession;
    }

    public async Task<ImplementationSearchResponse> FindImplementationsAsync(
        string filePath,
        int line,
        int character,
        string? content,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var absolutePath = _workspaceSession.GetAbsolutePath(filePath);
        await _workspaceSession.EnsureDocumentOpenAsync(absolutePath, content, cancellationToken);

        var hierarchyItems = await _lspClient.PrepareTypeHierarchyAsync(absolutePath, line, character, cancellationToken);
        var hierarchyRoot = hierarchyItems?.FirstOrDefault();
        if (hierarchyRoot != null && SupportsTypeHierarchyImplementations(hierarchyRoot.Kind))
        {
            var subtypes = await _lspClient.GetTypeHierarchySubtypesAsync(hierarchyRoot, cancellationToken) ?? Array.Empty<TypeHierarchyItem>();
            return new ImplementationSearchResponse(
                Summary: $"Found {subtypes.Length} implementation(s).",
                Root: MapHierarchyItem(hierarchyRoot),
                Implementations: subtypes.Take(Math.Max(1, maxResults)).Select(MapHierarchyItem).ToArray(),
                TruncatedImplementations: Math.Max(0, subtypes.Length - Math.Max(1, maxResults)));
        }

        var locations = await _lspClient.GetImplementationsAsync(absolutePath, line, character, cancellationToken);
        if (locations == null || locations.Length == 0)
        {
            return new ImplementationSearchResponse(
                Summary: "No implementations found.",
                Root: null,
                Implementations: Array.Empty<HierarchyNodeItem>(),
                TruncatedImplementations: 0);
        }

        var effectiveMaxResults = Math.Max(1, maxResults);
        return new ImplementationSearchResponse(
            Summary: $"Found {locations.Length} implementation(s).",
            Root: null,
            Implementations: locations.Take(effectiveMaxResults).Select(MapLocation).ToArray(),
            TruncatedImplementations: Math.Max(0, locations.Length - effectiveMaxResults));
    }

    public async Task<CallHierarchyResponse> GetCallHierarchyAsync(
        string filePath,
        int line,
        int character,
        string? content,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var absolutePath = _workspaceSession.GetAbsolutePath(filePath);
        await _workspaceSession.EnsureDocumentOpenAsync(absolutePath, content, cancellationToken);

        var items = await _lspClient.PrepareCallHierarchyAsync(absolutePath, line, character, cancellationToken);
        if (items == null || items.Length == 0)
        {
            return new CallHierarchyResponse(
                Summary: "No call hierarchy available at this position.",
                Root: null,
                Incoming: Array.Empty<CallHierarchyEdgeItem>(),
                Outgoing: Array.Empty<CallHierarchyEdgeItem>(),
                TruncatedIncoming: 0,
                TruncatedOutgoing: 0,
                PreparedRootCount: 0);
        }

        var item = items[0];
        var incoming = await _lspClient.GetIncomingCallsAsync(item, cancellationToken) ?? Array.Empty<CallHierarchyIncomingCall>();
        var outgoing = await _lspClient.GetOutgoingCallsAsync(item, cancellationToken) ?? Array.Empty<CallHierarchyOutgoingCall>();
        var effectiveMaxResults = Math.Max(1, maxResults);
        return new CallHierarchyResponse(
            Summary: $"Prepared call hierarchy for {item.Name} with {incoming.Length} incoming and {outgoing.Length} outgoing call(s).",
            Root: MapHierarchyItem(item),
            Incoming: incoming.Take(effectiveMaxResults)
                .Select(call => MapCallHierarchyEdge(call.From, call.FromRanges.FirstOrDefault()?.Start))
                .ToArray(),
            Outgoing: outgoing.Take(effectiveMaxResults)
                .Select(call => MapCallHierarchyEdge(call.To, call.FromRanges.FirstOrDefault()?.Start))
                .ToArray(),
            TruncatedIncoming: Math.Max(0, incoming.Length - effectiveMaxResults),
            TruncatedOutgoing: Math.Max(0, outgoing.Length - effectiveMaxResults),
            PreparedRootCount: items.Length);
    }

    public async Task<TypeHierarchyResponse> GetTypeHierarchyAsync(
        string filePath,
        int line,
        int character,
        string? content,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var absolutePath = _workspaceSession.GetAbsolutePath(filePath);
        await _workspaceSession.EnsureDocumentOpenAsync(absolutePath, content, cancellationToken);

        var items = await _lspClient.PrepareTypeHierarchyAsync(absolutePath, line, character, cancellationToken);
        if (items == null || items.Length == 0)
        {
            return new TypeHierarchyResponse(
                Summary: "No type hierarchy available at this position.",
                Root: null,
                Supertypes: Array.Empty<HierarchyNodeItem>(),
                Subtypes: Array.Empty<HierarchyNodeItem>(),
                TruncatedSupertypes: 0,
                TruncatedSubtypes: 0,
                PreparedRootCount: 0);
        }

        var item = items[0];
        var supertypes = await _lspClient.GetTypeHierarchySupertypesAsync(item, cancellationToken) ?? Array.Empty<TypeHierarchyItem>();
        var subtypes = await _lspClient.GetTypeHierarchySubtypesAsync(item, cancellationToken) ?? Array.Empty<TypeHierarchyItem>();
        var effectiveMaxResults = Math.Max(1, maxResults);
        return new TypeHierarchyResponse(
            Summary: $"Prepared type hierarchy for {item.Name} with {supertypes.Length} supertype(s) and {subtypes.Length} subtype(s).",
            Root: MapHierarchyItem(item),
            Supertypes: supertypes.Take(effectiveMaxResults).Select(MapHierarchyItem).ToArray(),
            Subtypes: subtypes.Take(effectiveMaxResults).Select(MapHierarchyItem).ToArray(),
            TruncatedSupertypes: Math.Max(0, supertypes.Length - effectiveMaxResults),
            TruncatedSubtypes: Math.Max(0, subtypes.Length - effectiveMaxResults),
            PreparedRootCount: items.Length);
    }

    private static HierarchyNodeItem MapHierarchyItem(TypeHierarchyItem item)
        => new(
            item.Name,
            item.Kind.ToString(),
            item.Detail,
            new Uri(item.Uri).LocalPath,
            item.SelectionRange.Start.Line + 1,
            item.SelectionRange.Start.Character + 1);

    private static HierarchyNodeItem MapHierarchyItem(CallHierarchyItem item)
        => new(
            item.Name,
            item.Kind.ToString(),
            item.Detail,
            new Uri(item.Uri).LocalPath,
            item.SelectionRange.Start.Line + 1,
            item.SelectionRange.Start.Character + 1);

    private static HierarchyNodeItem MapLocation(Location location)
        => new(
            Path.GetFileName(new Uri(location.Uri).LocalPath),
            "Location",
            null,
            new Uri(location.Uri).LocalPath,
            location.Range.Start.Line + 1,
            location.Range.Start.Character + 1);

    private static CallHierarchyEdgeItem MapCallHierarchyEdge(CallHierarchyItem item, Position? callSiteStart)
    {
        var location = callSiteStart ?? item.SelectionRange.Start;
        return new CallHierarchyEdgeItem(
            item.Name,
            item.Kind.ToString(),
            item.Detail,
            new Uri(item.Uri).LocalPath,
            location.Line + 1,
            location.Character + 1);
    }

    private static bool SupportsTypeHierarchyImplementations(SymbolKind kind)
        => kind is SymbolKind.Interface or SymbolKind.Class;

    public sealed record ImplementationSearchResponse(
        string Summary,
        HierarchyNodeItem? Root,
        HierarchyNodeItem[] Implementations,
        int TruncatedImplementations) : IStructuredToolResult;

    public sealed record CallHierarchyResponse(
        string Summary,
        HierarchyNodeItem? Root,
        CallHierarchyEdgeItem[] Incoming,
        CallHierarchyEdgeItem[] Outgoing,
        int TruncatedIncoming,
        int TruncatedOutgoing,
        int PreparedRootCount) : IStructuredToolResult;

    public sealed record TypeHierarchyResponse(
        string Summary,
        HierarchyNodeItem? Root,
        HierarchyNodeItem[] Supertypes,
        HierarchyNodeItem[] Subtypes,
        int TruncatedSupertypes,
        int TruncatedSubtypes,
        int PreparedRootCount) : IStructuredToolResult;

    public sealed record HierarchyNodeItem(
        string Name,
        string Kind,
        string? Detail,
        string FilePath,
        int Line,
        int Character);

    public sealed record CallHierarchyEdgeItem(
        string Name,
        string Kind,
        string? Detail,
        string FilePath,
        int Line,
        int Character);
}
