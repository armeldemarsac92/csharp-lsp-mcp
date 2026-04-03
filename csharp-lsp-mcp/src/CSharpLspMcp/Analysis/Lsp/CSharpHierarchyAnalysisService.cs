using System.Text;
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

    public async Task<string> FindImplementationsAsync(
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
            return FormatHierarchyImplementations(subtypes, maxResults);
        }

        var locations = await _lspClient.GetImplementationsAsync(absolutePath, line, character, cancellationToken);
        if (locations == null || locations.Length == 0)
            return "No implementations found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {locations.Length} implementation(s):\n");

        foreach (var location in locations.Take(maxResults))
        {
            var path = new Uri(location.Uri).LocalPath;
            sb.AppendLine($"• {path}");
            sb.AppendLine($"  Line {location.Range.Start.Line + 1}, Col {location.Range.Start.Character + 1}");
        }

        if (locations.Length > maxResults)
            sb.AppendLine($"\n... and {locations.Length - maxResults} more");

        return sb.ToString();
    }

    public async Task<string> GetCallHierarchyAsync(
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
            return "No call hierarchy available at this position.";

        var item = items[0];
        var incoming = await _lspClient.GetIncomingCallsAsync(item, cancellationToken) ?? Array.Empty<CallHierarchyIncomingCall>();
        var outgoing = await _lspClient.GetOutgoingCallsAsync(item, cancellationToken) ?? Array.Empty<CallHierarchyOutgoingCall>();

        var sb = new StringBuilder();
        sb.AppendLine($"Call hierarchy for {item.Name} ({item.Kind})");
        AppendHierarchyItemHeader(sb, item.Detail, item.Uri, item.SelectionRange.Start);

        if (items.Length > 1)
            sb.AppendLine($"Prepared {items.Length} hierarchy roots; showing the top-ranked match.\n");

        sb.AppendLine($"Incoming Calls ({incoming.Length}):");
        AppendIncomingCalls(sb, incoming, maxResults);

        sb.AppendLine($"\nOutgoing Calls ({outgoing.Length}):");
        AppendOutgoingCalls(sb, outgoing, maxResults);

        return sb.ToString();
    }

    public async Task<string> GetTypeHierarchyAsync(
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
            return "No type hierarchy available at this position.";

        var item = items[0];
        var supertypes = await _lspClient.GetTypeHierarchySupertypesAsync(item, cancellationToken) ?? Array.Empty<TypeHierarchyItem>();
        var subtypes = await _lspClient.GetTypeHierarchySubtypesAsync(item, cancellationToken) ?? Array.Empty<TypeHierarchyItem>();

        var sb = new StringBuilder();
        sb.AppendLine($"Type hierarchy for {item.Name} ({item.Kind})");
        AppendHierarchyItemHeader(sb, item.Detail, item.Uri, item.SelectionRange.Start);

        if (items.Length > 1)
            sb.AppendLine($"Prepared {items.Length} hierarchy roots; showing the top-ranked match.\n");

        sb.AppendLine($"Supertypes ({supertypes.Length}):");
        AppendTypeHierarchyItems(sb, supertypes, maxResults);

        sb.AppendLine($"\nSubtypes ({subtypes.Length}):");
        AppendTypeHierarchyItems(sb, subtypes, maxResults);

        return sb.ToString();
    }

    private static void AppendIncomingCalls(StringBuilder sb, IReadOnlyCollection<CallHierarchyIncomingCall> calls, int maxResults)
    {
        if (calls.Count == 0)
        {
            sb.AppendLine("None.");
            return;
        }

        foreach (var call in calls.Take(maxResults))
        {
            AppendCallSite(sb, call.From, call.FromRanges.FirstOrDefault()?.Start);
        }

        if (calls.Count > maxResults)
            sb.AppendLine($"... and {calls.Count - maxResults} more");
    }

    private static void AppendOutgoingCalls(StringBuilder sb, IReadOnlyCollection<CallHierarchyOutgoingCall> calls, int maxResults)
    {
        if (calls.Count == 0)
        {
            sb.AppendLine("None.");
            return;
        }

        foreach (var call in calls.Take(maxResults))
        {
            AppendCallSite(sb, call.To, call.FromRanges.FirstOrDefault()?.Start);
        }

        if (calls.Count > maxResults)
            sb.AppendLine($"... and {calls.Count - maxResults} more");
    }

    private static void AppendTypeHierarchyItems(StringBuilder sb, IReadOnlyCollection<TypeHierarchyItem> items, int maxResults)
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
            sb.AppendLine($"  {path}:{item.SelectionRange.Start.Line + 1}");
        }

        if (items.Count > maxResults)
            sb.AppendLine($"... and {items.Count - maxResults} more");
    }

    private static void AppendCallSite(StringBuilder sb, CallHierarchyItem item, Position? callSiteStart)
    {
        var path = new Uri(item.Uri).LocalPath;
        sb.AppendLine($"• {item.Name} ({item.Kind})");
        if (!string.IsNullOrWhiteSpace(item.Detail))
            sb.AppendLine($"  {item.Detail}");

        if (callSiteStart != null)
            sb.AppendLine($"  {path}:{callSiteStart.Line + 1}:{callSiteStart.Character + 1}");
        else
            sb.AppendLine($"  {path}:{item.SelectionRange.Start.Line + 1}:{item.SelectionRange.Start.Character + 1}");
    }

    private static void AppendHierarchyItemHeader(StringBuilder sb, string? detail, string uri, Position position)
    {
        if (!string.IsNullOrWhiteSpace(detail))
            sb.AppendLine(detail);

        var path = new Uri(uri).LocalPath;
        sb.AppendLine($"{path}:{position.Line + 1}:{position.Character + 1}\n");
    }

    private static string FormatHierarchyImplementations(IReadOnlyCollection<TypeHierarchyItem> items, int maxResults)
    {
        if (items.Count == 0)
            return "No implementations found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {items.Count} implementation(s):\n");

        foreach (var item in items.Take(maxResults))
        {
            var path = new Uri(item.Uri).LocalPath;
            sb.AppendLine($"• {item.Name} ({item.Kind})");
            if (!string.IsNullOrWhiteSpace(item.Detail))
                sb.AppendLine($"  {item.Detail}");
            sb.AppendLine($"  {path}:{item.SelectionRange.Start.Line + 1}");
        }

        if (items.Count > maxResults)
            sb.AppendLine($"\n... and {items.Count - maxResults} more");

        return sb.ToString();
    }

    private static bool SupportsTypeHierarchyImplementations(SymbolKind kind)
        => kind is SymbolKind.Interface or SymbolKind.Class;
}
