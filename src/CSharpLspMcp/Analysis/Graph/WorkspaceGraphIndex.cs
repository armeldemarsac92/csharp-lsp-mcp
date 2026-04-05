using CSharpLspMcp.Storage.Graph;

namespace CSharpLspMcp.Analysis.Graph;

internal sealed class WorkspaceGraphIndex
{
    private readonly Dictionary<string, WorkspaceGraphNode> _nodesById;
    private readonly Dictionary<string, WorkspaceGraphNode> _documentsByPath;
    private readonly Dictionary<string, List<WorkspaceGraphEdge>> _outgoingEdgesBySourceId;
    private readonly Dictionary<string, List<WorkspaceGraphEdge>> _incomingEdgesByTargetId;

    private WorkspaceGraphIndex(WorkspaceGraphSnapshot snapshot)
    {
        Snapshot = snapshot;
        _nodesById = snapshot.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        _documentsByPath = snapshot.Nodes
            .Where(node => string.Equals(node.Kind, WorkspaceGraphNodeKinds.Document, StringComparison.Ordinal))
            .Where(node => !string.IsNullOrWhiteSpace(node.FilePath))
            .ToDictionary(node => NormalizePath(node.FilePath!), StringComparer.Ordinal);
        _outgoingEdgesBySourceId = snapshot.Edges
            .GroupBy(edge => edge.SourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        _incomingEdgesByTargetId = snapshot.Edges
            .GroupBy(edge => edge.TargetId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
    }

    public WorkspaceGraphSnapshot Snapshot { get; }

    public static WorkspaceGraphIndex Create(WorkspaceGraphSnapshot snapshot)
        => new(snapshot);

    public WorkspaceGraphNode? GetNode(string id)
        => _nodesById.GetValueOrDefault(id);

    public WorkspaceGraphNode? GetDocument(string filePath)
        => _documentsByPath.GetValueOrDefault(NormalizePath(filePath));

    public IReadOnlyCollection<WorkspaceGraphNode> GetDeclaredSymbolsInDocument(string documentId)
    {
        if (!_incomingEdgesByTargetId.TryGetValue(documentId, out var edges))
            return Array.Empty<WorkspaceGraphNode>();

        return edges
            .Where(edge => string.Equals(edge.Kind, WorkspaceGraphEdgeKinds.DeclaredIn, StringComparison.Ordinal))
            .Select(edge => GetNode(edge.SourceId))
            .OfType<WorkspaceGraphNode>()
            .OrderBy(node => node.Line ?? int.MaxValue)
            .ThenBy(node => node.Character ?? int.MaxValue)
            .ThenBy(node => node.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyCollection<WorkspaceGraphNode> GetContainedNodes(string containerId)
    {
        if (!_outgoingEdgesBySourceId.TryGetValue(containerId, out var edges))
            return Array.Empty<WorkspaceGraphNode>();

        return edges
            .Where(edge => string.Equals(edge.Kind, WorkspaceGraphEdgeKinds.Contains, StringComparison.Ordinal))
            .Select(edge => GetNode(edge.TargetId))
            .OfType<WorkspaceGraphNode>()
            .OrderBy(node => node.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyCollection<WorkspaceGraphNode> GetIncomingSources(string targetId, string edgeKind)
    {
        if (!_incomingEdgesByTargetId.TryGetValue(targetId, out var edges))
            return Array.Empty<WorkspaceGraphNode>();

        return edges
            .Where(edge => string.Equals(edge.Kind, edgeKind, StringComparison.Ordinal))
            .Select(edge => GetNode(edge.SourceId))
            .OfType<WorkspaceGraphNode>()
            .OrderBy(node => node.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.FilePath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.Line ?? int.MaxValue)
            .ToArray();
    }

    public IReadOnlyCollection<WorkspaceGraphNode> GetOutgoingTargets(string sourceId, string edgeKind)
    {
        if (!_outgoingEdgesBySourceId.TryGetValue(sourceId, out var edges))
            return Array.Empty<WorkspaceGraphNode>();

        return edges
            .Where(edge => string.Equals(edge.Kind, edgeKind, StringComparison.Ordinal))
            .Select(edge => GetNode(edge.TargetId))
            .OfType<WorkspaceGraphNode>()
            .OrderBy(node => node.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.FilePath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.Line ?? int.MaxValue)
            .ToArray();
    }

    public IReadOnlyCollection<WorkspaceGraphNode> FindSymbolCandidates(string query, int maxResults)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<WorkspaceGraphNode>();

        var trimmedQuery = query.Trim();
        var payloadQuery = ExtractDocumentationPayload(trimmedQuery);
        var simpleName = GetTrailingIdentifier(payloadQuery);

        return Snapshot.Nodes
            .Where(node => IsSymbolNode(node.Kind))
            .Select(node => new
            {
                Node = node,
                Score = GetMatchScore(node, trimmedQuery, payloadQuery, simpleName)
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Node.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Node.FilePath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Node.Line ?? int.MaxValue)
            .Take(Math.Max(1, maxResults))
            .Select(item => item.Node)
            .ToArray();
    }

    private static bool IsSymbolNode(string kind)
        => !string.Equals(kind, WorkspaceGraphNodeKinds.Solution, StringComparison.Ordinal) &&
           !string.Equals(kind, WorkspaceGraphNodeKinds.Project, StringComparison.Ordinal) &&
           !string.Equals(kind, WorkspaceGraphNodeKinds.Document, StringComparison.Ordinal);

    private static int GetMatchScore(
        WorkspaceGraphNode node,
        string rawQuery,
        string payloadQuery,
        string simpleName)
    {
        var documentationPayload = ExtractDocumentationPayload(node.DocumentationId);
        var score = 0;

        if (!string.IsNullOrWhiteSpace(node.DocumentationId) &&
            string.Equals(node.DocumentationId, rawQuery, StringComparison.OrdinalIgnoreCase))
        {
            score = Math.Max(score, 300);
        }

        if (!string.IsNullOrWhiteSpace(documentationPayload))
        {
            if (string.Equals(documentationPayload, payloadQuery, StringComparison.OrdinalIgnoreCase))
                score = Math.Max(score, 280);
            else if (documentationPayload.EndsWith($".{payloadQuery}", StringComparison.OrdinalIgnoreCase))
                score = Math.Max(score, 240);
            else if (documentationPayload.Contains(payloadQuery, StringComparison.OrdinalIgnoreCase))
                score = Math.Max(score, 180);
        }

        if (!string.IsNullOrWhiteSpace(node.MetadataName))
        {
            if (string.Equals(node.MetadataName, simpleName, StringComparison.OrdinalIgnoreCase))
                score = Math.Max(score, 160);
            else if (node.MetadataName.Contains(simpleName, StringComparison.OrdinalIgnoreCase))
                score = Math.Max(score, 120);
        }

        if (string.Equals(node.DisplayName, payloadQuery, StringComparison.OrdinalIgnoreCase))
            score = Math.Max(score, 140);
        else if (!string.IsNullOrWhiteSpace(simpleName) &&
                 node.DisplayName.Contains(simpleName, StringComparison.OrdinalIgnoreCase))
            score = Math.Max(score, 100);

        return score;
    }

    private static string ExtractDocumentationPayload(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var trimmed = value.Trim();
        var separatorIndex = trimmed.IndexOf(':');
        return separatorIndex >= 0 && separatorIndex < trimmed.Length - 1
            ? trimmed[(separatorIndex + 1)..]
            : trimmed;
    }

    private static string GetTrailingIdentifier(string value)
    {
        var sanitized = value;
        var parameterIndex = sanitized.IndexOf('(');
        if (parameterIndex >= 0)
            sanitized = sanitized[..parameterIndex];

        var lastSeparator = sanitized.LastIndexOf('.');
        return lastSeparator >= 0 && lastSeparator < sanitized.Length - 1
            ? sanitized[(lastSeparator + 1)..]
            : sanitized;
    }

    private static string NormalizePath(string filePath)
        => Path.GetFullPath(filePath).Replace('\\', '/');
}
