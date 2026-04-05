using System.Text;
using CSharpLspMcp.Contracts.Common;
using CSharpLspMcp.Storage.Graph;
using CSharpLspMcp.Workspace;

namespace CSharpLspMcp.Analysis.Graph;

public sealed class CSharpGraphProjectionService
{
    private static readonly string[] DefaultEdgeKinds =
    [
        WorkspaceGraphEdgeKinds.Contains,
        WorkspaceGraphEdgeKinds.DependsOnProject,
        WorkspaceGraphEdgeKinds.Inherits,
        WorkspaceGraphEdgeKinds.Implements,
        WorkspaceGraphEdgeKinds.Overrides,
        WorkspaceGraphEdgeKinds.Calls,
        WorkspaceGraphEdgeKinds.RegisteredAs,
        WorkspaceGraphEdgeKinds.ConsumedBy
    ];

    private readonly CSharpGraphBuildService _graphBuildService;
    private readonly CSharpGraphCacheStore _graphCacheStore;
    private readonly WorkspaceState _workspaceState;

    public CSharpGraphProjectionService(
        CSharpGraphBuildService graphBuildService,
        CSharpGraphCacheStore graphCacheStore,
        WorkspaceState workspaceState)
    {
        _graphBuildService = graphBuildService;
        _graphCacheStore = graphCacheStore;
        _workspaceState = workspaceState;
    }

    public async Task<GraphProjectionResponse> ExportAsync(
        string? path,
        string layout,
        string? focusSymbol,
        string? projectFilter,
        bool includeTypes,
        bool includeMembers,
        bool includeDocuments,
        bool includeDi,
        bool includeEntrypoints,
        string[]? edgeKinds,
        int maxNodes,
        int maxEdges,
        bool rebuildIfMissing,
        bool writeToFile,
        string? outputPath,
        CancellationToken cancellationToken)
    {
        var workspaceInput = ResolveWorkspaceInput(path);
        var workspaceRoot = NormalizeWorkspaceRoot(workspaceInput);
        var warnings = new List<string>();
        var normalizedLayout = NormalizeLayout(layout);
        var effectiveMaxNodes = Math.Max(5, maxNodes);
        var effectiveMaxEdges = Math.Max(5, maxEdges);
        var snapshotState = await EnsureSnapshotAsync(workspaceInput, workspaceRoot, rebuildIfMissing, warnings, cancellationToken);
        var snapshot = snapshotState.Snapshot;
        var index = WorkspaceGraphIndex.Create(snapshot);
        var normalizedEdgeKinds = NormalizeEdgeKinds(edgeKinds);

        var selection = BuildSelection(
            snapshot,
            index,
            workspaceRoot,
            focusSymbol,
            projectFilter,
            includeTypes,
            includeMembers,
            includeDocuments,
            includeDi,
            includeEntrypoints,
            normalizedEdgeKinds,
            effectiveMaxNodes,
            effectiveMaxEdges);

        warnings.AddRange(selection.Warnings);
        var projection = string.Equals(normalizedLayout, "dot", StringComparison.OrdinalIgnoreCase)
            ? RenderDot(selection)
            : RenderMermaid(selection);
        var mode = !string.IsNullOrWhiteSpace(focusSymbol)
            ? "focus"
            : !string.IsNullOrWhiteSpace(projectFilter)
                ? "project"
                : "overview";
        var exportFilePath = writeToFile
            ? await WriteProjectionAsync(workspaceRoot, normalizedLayout, mode, focusSymbol, projectFilter, outputPath, projection, cancellationToken)
            : null;
        var summary = $"Rendered {selection.RenderedNodes.Length} node(s) and {selection.RenderedEdges.Length} edge(s) as {normalizedLayout} from the persisted graph ({mode} mode).";
        if (!string.IsNullOrWhiteSpace(exportFilePath))
            summary += $" Exported to {NormalizeResponseFilePath(exportFilePath, workspaceRoot)}.";

        return new GraphProjectionResponse(
            Summary: summary,
            Layout: normalizedLayout,
            Mode: mode,
            WorkspaceRoot: snapshot.WorkspaceRoot,
            WorkspaceTargetPath: snapshot.WorkspaceTargetPath,
            GraphBuiltDuringRequest: snapshotState.GraphBuiltDuringRequest,
            GraphBuiltAtUtc: snapshot.BuiltAtUtc,
            FocusSymbol: focusSymbol,
            ProjectFilter: projectFilter,
            IncludeTypes: includeTypes,
            IncludeMembers: includeMembers,
            IncludeDocuments: includeDocuments,
            IncludeDi: includeDi,
            IncludeEntrypoints: includeEntrypoints,
            EdgeKinds: normalizedEdgeKinds,
            WriteToFile: writeToFile,
            ExportPath: string.IsNullOrWhiteSpace(exportFilePath)
                ? null
                : NormalizeResponseFilePath(exportFilePath, workspaceRoot),
            Projection: projection,
            Nodes: selection.RenderedNodes,
            Edges: selection.RenderedEdges,
            TotalCandidateNodes: selection.TotalCandidateNodes,
            TotalCandidateEdges: selection.TotalCandidateEdges,
            TruncatedNodes: Math.Max(0, selection.TotalCandidateNodes - selection.RenderedNodes.Length),
            TruncatedEdges: Math.Max(0, selection.TotalCandidateEdges - selection.RenderedEdges.Length),
            Warnings: warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private async Task<SnapshotState> EnsureSnapshotAsync(
        string workspaceInput,
        string workspaceRoot,
        bool rebuildIfMissing,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var snapshot = await _graphCacheStore.LoadAsync(workspaceRoot, cancellationToken);
        var graphBuiltDuringRequest = false;

        if (snapshot == null)
        {
            if (!rebuildIfMissing)
                throw new InvalidOperationException("No persisted code graph is available. Run csharp_build_code_graph first or enable rebuildIfMissing.");

            warnings.Add("No persisted graph was found. A new graph was built for projection export.");
            await _graphBuildService.BuildAsync(
                workspaceInput,
                mode: "incremental",
                includeTests: true,
                includeGenerated: false,
                cancellationToken);
            graphBuiltDuringRequest = true;
            snapshot = await _graphCacheStore.LoadAsync(workspaceRoot, cancellationToken);
        }

        if (snapshot == null)
            throw new InvalidOperationException("No persisted code graph is available for the current workspace.");

        return new SnapshotState(snapshot, graphBuiltDuringRequest);
    }

    private static SelectionResult BuildSelection(
        WorkspaceGraphSnapshot snapshot,
        WorkspaceGraphIndex index,
        string workspaceRoot,
        string? focusSymbol,
        string? projectFilter,
        bool includeTypes,
        bool includeMembers,
        bool includeDocuments,
        bool includeDi,
        bool includeEntrypoints,
        IReadOnlyCollection<string> edgeKinds,
        int maxNodes,
        int maxEdges)
    {
        var nodeScores = new Dictionary<string, int>(StringComparer.Ordinal);
        var nodeReasons = new Dictionary<string, string>(StringComparer.Ordinal);
        var warnings = new List<string>();

        if (!string.IsNullOrWhiteSpace(focusSymbol))
        {
            var focusMatches = FilterPreferredSymbolMatches(index.FindSymbolCandidates(focusSymbol, 8), focusSymbol);
            if (focusMatches.Count == 0)
            {
                warnings.Add($"No graph symbol matched '{focusSymbol}'. Falling back to an overview slice.");
            }
            else
            {
                foreach (var match in focusMatches)
                {
                    AddNode(nodeScores, nodeReasons, match.Id, 1000, "focus");
                    AddContainmentContext(index, match.Id, nodeScores, nodeReasons);
                    AddFocusNeighbors(index, match.Id, edgeKinds, includeTypes, includeMembers, includeDocuments, includeDi, includeEntrypoints, nodeScores, nodeReasons);
                    AddSameProjectHostEntrypoints(snapshot, match.ProjectName, nodeScores, nodeReasons);
                }
            }
        }

        if (nodeScores.Count == 0 && !string.IsNullOrWhiteSpace(projectFilter))
        {
            var matchingProjects = FilterPreferredProjectMatches(snapshot.Projects, projectFilter);
            if (matchingProjects.Length == 0)
            {
                warnings.Add($"No graph project matched '{projectFilter}'. Falling back to an overview slice.");
            }
            else
            {
                foreach (var project in matchingProjects)
                {
                    AddNode(nodeScores, nodeReasons, project.Id, 1000, "project");
                    AddProjectDependencyNeighbors(index, project.Id, nodeScores, nodeReasons);

                    foreach (var node in snapshot.Nodes.Where(node => string.Equals(node.OwningProjectId, project.Id, StringComparison.Ordinal)))
                    {
                        if (!ShouldIncludeNodeKind(node.Kind, includeTypes, includeMembers, includeDocuments, includeDi, includeEntrypoints, allowSymbolSeeds: false))
                            continue;

                        AddNode(nodeScores, nodeReasons, node.Id, GetNodePriority(node.Kind), "project-slice");
                        if (string.Equals(node.Kind, WorkspaceGraphNodeKinds.Type, StringComparison.Ordinal) ||
                            string.Equals(node.Kind, WorkspaceGraphNodeKinds.Method, StringComparison.Ordinal) ||
                            string.Equals(node.Kind, WorkspaceGraphNodeKinds.Property, StringComparison.Ordinal) ||
                            string.Equals(node.Kind, WorkspaceGraphNodeKinds.Event, StringComparison.Ordinal))
                        {
                            AddContainmentContext(index, node.Id, nodeScores, nodeReasons);
                        }
                    }
                }
            }
        }

        if (nodeScores.Count == 0)
        {
            var solutionNode = snapshot.Nodes.FirstOrDefault(node => string.Equals(node.Kind, WorkspaceGraphNodeKinds.Solution, StringComparison.Ordinal));
            if (solutionNode != null)
                AddNode(nodeScores, nodeReasons, solutionNode.Id, 1000, "solution");

            foreach (var project in snapshot.Projects.OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase))
                AddNode(nodeScores, nodeReasons, project.Id, 900, "project");

            foreach (var node in snapshot.Nodes)
            {
                if (!ShouldIncludeNodeKind(node.Kind, includeTypes: false, includeMembers: false, includeDocuments: false, includeDi, includeEntrypoints, allowSymbolSeeds: false))
                    continue;

                AddNode(nodeScores, nodeReasons, node.Id, GetNodePriority(node.Kind), "overview");
            }
        }

        var rankedNodes = nodeScores.Keys
            .Select(id => snapshot.Nodes.First(node => string.Equals(node.Id, id, StringComparison.Ordinal)))
            .OrderByDescending(node => nodeScores[node.Id])
            .ThenByDescending(node => GetNodePriority(node.Kind))
            .ThenBy(node => node.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.FilePath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var renderedNodes = rankedNodes
            .Take(maxNodes)
            .Select((node, indexPosition) => new GraphProjectionNodeItem(
                GraphId: $"n{indexPosition}",
                NodeId: node.Id,
                Kind: node.Kind,
                Label: BuildNodeLabel(node),
                ProjectName: node.ProjectName,
                RelativePath: NormalizeResponseFilePath(node.FilePath, workspaceRoot),
                Line: node.Line,
                Reason: nodeReasons.GetValueOrDefault(node.Id)))
            .ToArray();

        var renderedNodeIds = renderedNodes
            .Select(node => node.NodeId)
            .ToHashSet(StringComparer.Ordinal);
        var graphIdByNodeId = renderedNodes.ToDictionary(node => node.NodeId, node => node.GraphId, StringComparer.Ordinal);

        var renderedEdges = snapshot.Edges
            .Where(edge => edgeKinds.Contains(edge.Kind, StringComparer.OrdinalIgnoreCase))
            .Where(edge => renderedNodeIds.Contains(edge.SourceId) && renderedNodeIds.Contains(edge.TargetId))
            .OrderByDescending(edge => GetEdgePriority(edge.Kind))
            .ThenBy(edge => edge.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(edge => edge.SourceId, StringComparer.Ordinal)
            .ThenBy(edge => edge.TargetId, StringComparer.Ordinal)
            .Take(maxEdges)
            .Select(edge => new GraphProjectionEdgeItem(
                Kind: edge.Kind,
                SourceGraphId: graphIdByNodeId[edge.SourceId],
                TargetGraphId: graphIdByNodeId[edge.TargetId],
                SourceNodeId: edge.SourceId,
                TargetNodeId: edge.TargetId))
            .ToArray();

        var totalCandidateEdges = snapshot.Edges.Count(edge =>
            edgeKinds.Contains(edge.Kind, StringComparer.OrdinalIgnoreCase) &&
            renderedNodeIds.Contains(edge.SourceId) &&
            renderedNodeIds.Contains(edge.TargetId));

        return new SelectionResult(
            TotalCandidateNodes: rankedNodes.Length,
            TotalCandidateEdges: totalCandidateEdges,
            RenderedNodes: renderedNodes,
            RenderedEdges: renderedEdges,
            Warnings: warnings.ToArray());
    }

    private static void AddFocusNeighbors(
        WorkspaceGraphIndex index,
        string nodeId,
        IReadOnlyCollection<string> edgeKinds,
        bool includeTypes,
        bool includeMembers,
        bool includeDocuments,
        bool includeDi,
        bool includeEntrypoints,
        IDictionary<string, int> nodeScores,
        IDictionary<string, string> nodeReasons)
    {
        foreach (var edgeKind in edgeKinds)
        {
            foreach (var outgoing in index.GetOutgoingTargets(nodeId, edgeKind))
            {
                if (!ShouldIncludeNodeKind(outgoing.Kind, includeTypes, includeMembers, includeDocuments, includeDi, includeEntrypoints, allowSymbolSeeds: true))
                    continue;

                AddNode(nodeScores, nodeReasons, outgoing.Id, 800, edgeKind);
                AddContainmentContext(index, outgoing.Id, nodeScores, nodeReasons);
            }

            foreach (var incoming in index.GetIncomingSources(nodeId, edgeKind))
            {
                if (!ShouldIncludeNodeKind(incoming.Kind, includeTypes, includeMembers, includeDocuments, includeDi, includeEntrypoints, allowSymbolSeeds: true))
                    continue;

                AddNode(nodeScores, nodeReasons, incoming.Id, 800, edgeKind);
                AddContainmentContext(index, incoming.Id, nodeScores, nodeReasons);
            }
        }
    }

    private static void AddSameProjectHostEntrypoints(
        WorkspaceGraphSnapshot snapshot,
        string projectName,
        IDictionary<string, int> nodeScores,
        IDictionary<string, string> nodeReasons)
    {
        foreach (var node in snapshot.Nodes.Where(node =>
                     string.Equals(node.Kind, WorkspaceGraphNodeKinds.Entrypoint, StringComparison.Ordinal) &&
                     string.Equals(node.ProjectName, projectName, StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(ExtractEntrypointCategory(node.DocumentationId), "host_project", StringComparison.OrdinalIgnoreCase)))
        {
            AddNode(nodeScores, nodeReasons, node.Id, 400, "host_project");
        }
    }

    private static void AddProjectDependencyNeighbors(
        WorkspaceGraphIndex index,
        string projectId,
        IDictionary<string, int> nodeScores,
        IDictionary<string, string> nodeReasons)
    {
        foreach (var dependency in index.GetOutgoingTargets(projectId, WorkspaceGraphEdgeKinds.DependsOnProject))
            AddNode(nodeScores, nodeReasons, dependency.Id, 850, "depends_on_project");

        foreach (var dependent in index.GetIncomingSources(projectId, WorkspaceGraphEdgeKinds.DependsOnProject))
            AddNode(nodeScores, nodeReasons, dependent.Id, 850, "depended_on_by");
    }

    private static void AddContainmentContext(
        WorkspaceGraphIndex index,
        string nodeId,
        IDictionary<string, int> nodeScores,
        IDictionary<string, string> nodeReasons)
    {
        foreach (var container in index.GetIncomingSources(nodeId, WorkspaceGraphEdgeKinds.Contains))
        {
            AddNode(nodeScores, nodeReasons, container.Id, GetNodePriority(container.Kind) + 50, "context");
            AddContainmentContext(index, container.Id, nodeScores, nodeReasons);
        }
    }

    private static void AddNode(
        IDictionary<string, int> nodeScores,
        IDictionary<string, string> nodeReasons,
        string nodeId,
        int score,
        string reason)
    {
        if (nodeScores.TryGetValue(nodeId, out var currentScore))
        {
            if (score > currentScore)
            {
                nodeScores[nodeId] = score;
                nodeReasons[nodeId] = reason;
            }

            return;
        }

        nodeScores[nodeId] = score;
        nodeReasons[nodeId] = reason;
    }

    private static bool MatchesProjectFilter(WorkspaceGraphProjectSummary project, string projectFilter)
    {
        var filter = projectFilter.Trim();
        return project.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               project.FilePath.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               project.AssemblyName.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static WorkspaceGraphProjectSummary[] FilterPreferredProjectMatches(
        IReadOnlyCollection<WorkspaceGraphProjectSummary> projects,
        string projectFilter)
    {
        var matches = projects
            .Where(project => MatchesProjectFilter(project, projectFilter))
            .ToArray();
        if (matches.Length <= 1)
            return matches;

        var filter = projectFilter.Trim();
        var exactMatches = matches
            .Where(project =>
                string.Equals(project.Name, filter, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(project.AssemblyName, filter, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileNameWithoutExtension(project.FilePath), filter, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (exactMatches.Length > 0)
            return exactMatches;

        return matches;
    }

    private static bool ShouldIncludeNodeKind(
        string kind,
        bool includeTypes,
        bool includeMembers,
        bool includeDocuments,
        bool includeDi,
        bool includeEntrypoints,
        bool allowSymbolSeeds)
    {
        if (string.Equals(kind, WorkspaceGraphNodeKinds.Solution, StringComparison.Ordinal) ||
            string.Equals(kind, WorkspaceGraphNodeKinds.Project, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(kind, WorkspaceGraphNodeKinds.Namespace, StringComparison.Ordinal))
            return includeTypes || includeMembers || allowSymbolSeeds;

        if (string.Equals(kind, WorkspaceGraphNodeKinds.Document, StringComparison.Ordinal))
            return includeDocuments;

        if (string.Equals(kind, WorkspaceGraphNodeKinds.DiRegistration, StringComparison.Ordinal))
            return includeDi;

        if (string.Equals(kind, WorkspaceGraphNodeKinds.Entrypoint, StringComparison.Ordinal))
            return includeEntrypoints;

        if (string.Equals(kind, WorkspaceGraphNodeKinds.Type, StringComparison.Ordinal))
            return includeTypes || includeMembers || allowSymbolSeeds;

        return includeMembers || allowSymbolSeeds;
    }

    private static string NormalizeLayout(string layout)
    {
        var normalized = layout.Trim().ToLowerInvariant();
        return normalized switch
        {
            "mermaid" => "mermaid",
            "dot" => "dot",
            _ => throw new InvalidOperationException("layout must be either 'mermaid' or 'dot'.")
        };
    }

    private static string[] NormalizeEdgeKinds(string[]? edgeKinds)
    {
        var normalized = (edgeKinds == null || edgeKinds.Length == 0
                ? DefaultEdgeKinds
                : edgeKinds)
            .Where(edgeKind => !string.IsNullOrWhiteSpace(edgeKind))
            .Select(edgeKind => edgeKind.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var edgeKind in normalized)
        {
            if (!IsSupportedEdgeKind(edgeKind))
                throw new InvalidOperationException($"Unsupported edge kind '{edgeKind}'.");
        }

        return normalized;
    }

    private static bool IsSupportedEdgeKind(string edgeKind)
        => edgeKind is WorkspaceGraphEdgeKinds.Contains or
            WorkspaceGraphEdgeKinds.DeclaredIn or
            WorkspaceGraphEdgeKinds.DependsOnProject or
            WorkspaceGraphEdgeKinds.Inherits or
            WorkspaceGraphEdgeKinds.Implements or
            WorkspaceGraphEdgeKinds.Overrides or
            WorkspaceGraphEdgeKinds.Calls or
            WorkspaceGraphEdgeKinds.RegisteredAs or
            WorkspaceGraphEdgeKinds.ConsumedBy;

    private static IReadOnlyCollection<WorkspaceGraphNode> FilterPreferredSymbolMatches(
        IReadOnlyCollection<WorkspaceGraphNode> symbolMatches,
        string symbolQuery)
    {
        if (symbolMatches.Count <= 1)
            return symbolMatches;

        var matches = symbolMatches.ToArray();
        var payloadQuery = ExtractDocumentationPayload(symbolQuery);
        var simpleName = GetTrailingIdentifier(payloadQuery);

        var identityMatches = matches
            .Where(node => IsStrongIdentityMatch(node, symbolQuery, payloadQuery, simpleName))
            .ToArray();
        if (identityMatches.Length > 0)
            return identityMatches;

        var simpleMatches = matches
            .Where(node => HasExactSimpleIdentity(node, simpleName))
            .ToArray();
        if (simpleMatches.Length > 0)
            return simpleMatches;

        return matches;
    }

    private static bool IsStrongIdentityMatch(
        WorkspaceGraphNode node,
        string rawQuery,
        string payloadQuery,
        string simpleName)
    {
        if (!string.IsNullOrWhiteSpace(node.DocumentationId) &&
            string.Equals(node.DocumentationId, rawQuery, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var payload = ExtractDocumentationPayload(node.DocumentationId);
        if (!string.IsNullOrWhiteSpace(payload))
        {
            if (string.Equals(payload, payloadQuery, StringComparison.OrdinalIgnoreCase))
                return true;

            if (payload.EndsWith($".{payloadQuery}", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return HasExactSimpleIdentity(node, simpleName);
    }

    private static bool HasExactSimpleIdentity(WorkspaceGraphNode node, string simpleName)
    {
        if (string.IsNullOrWhiteSpace(simpleName))
            return false;

        if (!string.IsNullOrWhiteSpace(node.MetadataName) &&
            string.Equals(node.MetadataName, simpleName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var payload = ExtractDocumentationPayload(node.DocumentationId);
        return !string.IsNullOrWhiteSpace(payload) &&
               string.Equals(GetTrailingIdentifier(payload), simpleName, StringComparison.OrdinalIgnoreCase);
    }

    private static string RenderMermaid(SelectionResult selection)
    {
        var builder = new StringBuilder();
        builder.AppendLine("flowchart LR");

        foreach (var node in selection.RenderedNodes)
        {
            builder.Append("  ")
                .Append(node.GraphId)
                .Append("[\"")
                .Append(EscapeMermaid(node.Label))
                .AppendLine("\"]");
        }

        foreach (var edge in selection.RenderedEdges)
        {
            builder.Append("  ")
                .Append(edge.SourceGraphId)
                .Append(" -->|")
                .Append(EscapeMermaid(edge.Kind))
                .Append("| ")
                .Append(edge.TargetGraphId)
                .AppendLine();
        }

        builder.AppendLine();
        builder.AppendLine("  classDef solution fill:#f3f4f6,stroke:#4b5563,color:#111827;");
        builder.AppendLine("  classDef project fill:#dbeafe,stroke:#1d4ed8,color:#0f172a;");
        builder.AppendLine("  classDef namespace fill:#ede9fe,stroke:#7c3aed,color:#1f2937;");
        builder.AppendLine("  classDef type fill:#dcfce7,stroke:#15803d,color:#14532d;");
        builder.AppendLine("  classDef member fill:#fef3c7,stroke:#d97706,color:#78350f;");
        builder.AppendLine("  classDef document fill:#fee2e2,stroke:#b91c1c,color:#7f1d1d;");
        builder.AppendLine("  classDef di fill:#fce7f3,stroke:#be185d,color:#831843;");
        builder.AppendLine("  classDef entrypoint fill:#cffafe,stroke:#0891b2,color:#164e63;");

        foreach (var node in selection.RenderedNodes)
        {
            builder.Append("  class ")
                .Append(node.GraphId)
                .Append(' ')
                .Append(GetMermaidClass(node.Kind))
                .AppendLine(";");
        }

        return builder.ToString().TrimEnd();
    }

    private static string RenderDot(SelectionResult selection)
    {
        var builder = new StringBuilder();
        builder.AppendLine("digraph CSharpCodeGraph {");
        builder.AppendLine("  rankdir=LR;");
        builder.AppendLine("  graph [fontname=\"Helvetica\"];");
        builder.AppendLine("  node [fontname=\"Helvetica\", style=\"rounded,filled\"];");
        builder.AppendLine("  edge [fontname=\"Helvetica\"];");

        foreach (var node in selection.RenderedNodes)
        {
            var (shape, color) = GetDotStyle(node.Kind);
            builder.Append("  ")
                .Append(node.GraphId)
                .Append(" [label=\"")
                .Append(EscapeDot(node.Label))
                .Append("\", shape=")
                .Append(shape)
                .Append(", fillcolor=\"")
                .Append(color)
                .AppendLine("\"];");
        }

        foreach (var edge in selection.RenderedEdges)
        {
            builder.Append("  ")
                .Append(edge.SourceGraphId)
                .Append(" -> ")
                .Append(edge.TargetGraphId)
                .Append(" [label=\"")
                .Append(EscapeDot(edge.Kind))
                .AppendLine("\"];");
        }

        builder.Append('}');
        return builder.ToString();
    }

    private static string GetMermaidClass(string kind)
        => kind switch
        {
            WorkspaceGraphNodeKinds.Solution => "solution",
            WorkspaceGraphNodeKinds.Project => "project",
            WorkspaceGraphNodeKinds.Namespace => "namespace",
            WorkspaceGraphNodeKinds.Type => "type",
            WorkspaceGraphNodeKinds.Document => "document",
            WorkspaceGraphNodeKinds.DiRegistration => "di",
            WorkspaceGraphNodeKinds.Entrypoint => "entrypoint",
            _ => "member"
        };

    private static (string Shape, string FillColor) GetDotStyle(string kind)
        => kind switch
        {
            WorkspaceGraphNodeKinds.Solution => ("box", "#f3f4f6"),
            WorkspaceGraphNodeKinds.Project => ("box", "#dbeafe"),
            WorkspaceGraphNodeKinds.Namespace => ("box", "#ede9fe"),
            WorkspaceGraphNodeKinds.Type => ("ellipse", "#dcfce7"),
            WorkspaceGraphNodeKinds.Document => ("note", "#fee2e2"),
            WorkspaceGraphNodeKinds.DiRegistration => ("parallelogram", "#fce7f3"),
            WorkspaceGraphNodeKinds.Entrypoint => ("hexagon", "#cffafe"),
            _ => ("ellipse", "#fef3c7")
        };

    private static string BuildNodeLabel(WorkspaceGraphNode node)
    {
        var kindLabel = node.Kind switch
        {
            WorkspaceGraphNodeKinds.Solution => "Solution",
            WorkspaceGraphNodeKinds.Project => "Project",
            WorkspaceGraphNodeKinds.Namespace => "Namespace",
            WorkspaceGraphNodeKinds.Type => "Type",
            WorkspaceGraphNodeKinds.Method => "Method",
            WorkspaceGraphNodeKinds.Property => "Property",
            WorkspaceGraphNodeKinds.Field => "Field",
            WorkspaceGraphNodeKinds.Event => "Event",
            WorkspaceGraphNodeKinds.Document => "Document",
            WorkspaceGraphNodeKinds.DiRegistration => "DI",
            WorkspaceGraphNodeKinds.Entrypoint => "Entrypoint",
            _ => node.Kind
        };

        return $"{kindLabel}: {node.DisplayName}";
    }

    private static int GetNodePriority(string kind)
        => kind switch
        {
            WorkspaceGraphNodeKinds.Solution => 100,
            WorkspaceGraphNodeKinds.Project => 95,
            WorkspaceGraphNodeKinds.Entrypoint => 90,
            WorkspaceGraphNodeKinds.DiRegistration => 85,
            WorkspaceGraphNodeKinds.Type => 80,
            WorkspaceGraphNodeKinds.Namespace => 75,
            WorkspaceGraphNodeKinds.Method => 70,
            WorkspaceGraphNodeKinds.Property => 68,
            WorkspaceGraphNodeKinds.Event => 67,
            WorkspaceGraphNodeKinds.Field => 65,
            WorkspaceGraphNodeKinds.Document => 60,
            _ => 10
        };

    private static int GetEdgePriority(string kind)
        => kind switch
        {
            WorkspaceGraphEdgeKinds.DependsOnProject => 100,
            WorkspaceGraphEdgeKinds.Contains => 90,
            WorkspaceGraphEdgeKinds.RegisteredAs => 85,
            WorkspaceGraphEdgeKinds.ConsumedBy => 80,
            WorkspaceGraphEdgeKinds.Implements => 75,
            WorkspaceGraphEdgeKinds.Inherits => 70,
            WorkspaceGraphEdgeKinds.Overrides => 65,
            WorkspaceGraphEdgeKinds.Calls => 60,
            WorkspaceGraphEdgeKinds.DeclaredIn => 40,
            _ => 10
        };

    private static string ExtractDocumentationPayload(string? documentationId)
    {
        if (string.IsNullOrWhiteSpace(documentationId))
            return string.Empty;

        var separatorIndex = documentationId.IndexOf(':');
        return separatorIndex >= 0 && separatorIndex < documentationId.Length - 1
            ? documentationId[(separatorIndex + 1)..]
            : documentationId;
    }

    private static string GetTrailingIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var parameterIndex = value.IndexOf('(');
        var trimmed = parameterIndex >= 0 ? value[..parameterIndex] : value;
        var lastSeparator = trimmed.LastIndexOf('.');
        return lastSeparator >= 0 && lastSeparator < trimmed.Length - 1
            ? trimmed[(lastSeparator + 1)..]
            : trimmed;
    }

    private static string ExtractEntrypointCategory(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return string.Empty;

        var parts = payload.Split('|');
        return parts.Length >= 2 && string.Equals(parts[0], "entrypoint", StringComparison.Ordinal)
            ? parts[1]
            : string.Empty;
    }

    private static string EscapeMermaid(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", "<br/>", StringComparison.Ordinal);

    private static string EscapeDot(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private string ResolveWorkspaceInput(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            return Path.GetFullPath(path);

        if (!string.IsNullOrWhiteSpace(_workspaceState.CurrentPath))
            return _workspaceState.CurrentPath;

        throw new InvalidOperationException("Workspace is not set. Provide path or call csharp_set_workspace first.");
    }

    private static async Task<string> WriteProjectionAsync(
        string workspaceRoot,
        string layout,
        string mode,
        string? focusSymbol,
        string? projectFilter,
        string? outputPath,
        string projection,
        CancellationToken cancellationToken)
    {
        var resolvedPath = ResolveExportPath(workspaceRoot, layout, mode, focusSymbol, projectFilter, outputPath);
        var directory = Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(resolvedPath, projection, cancellationToken);
        return resolvedPath;
    }

    private static string ResolveExportPath(
        string workspaceRoot,
        string layout,
        string mode,
        string? focusSymbol,
        string? projectFilter,
        string? outputPath)
    {
        var extension = string.Equals(layout, "dot", StringComparison.OrdinalIgnoreCase)
            ? ".dot"
            : ".mmd";
        var candidatePath = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(
                workspaceRoot,
                ".csharp-lsp-mcp",
                "exports",
                $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{mode}-{BuildExportSlug(focusSymbol, projectFilter)}{extension}")
            : outputPath!;
        var fullPath = Path.IsPathRooted(candidatePath)
            ? Path.GetFullPath(candidatePath)
            : Path.GetFullPath(Path.Combine(workspaceRoot, candidatePath));

        return string.Equals(Path.GetExtension(fullPath), extension, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath + extension;
    }

    private static string BuildExportSlug(string? focusSymbol, string? projectFilter)
    {
        var basis = !string.IsNullOrWhiteSpace(focusSymbol)
            ? GetTrailingIdentifier(ExtractDocumentationPayload(focusSymbol))
            : !string.IsNullOrWhiteSpace(projectFilter)
                ? projectFilter
                : "overview";
        var cleaned = new string(basis
            .Trim()
            .Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-')
            .ToArray())
            .Trim('-');
        return string.IsNullOrWhiteSpace(cleaned) ? "graph" : cleaned;
    }

    private static string NormalizeWorkspaceRoot(string workspaceInput)
    {
        var fullPath = Path.GetFullPath(workspaceInput);
        if (Directory.Exists(fullPath))
            return fullPath;

        if (File.Exists(fullPath))
            return Path.GetDirectoryName(fullPath)!;

        return Path.GetExtension(fullPath).Length > 0
            ? Path.GetDirectoryName(fullPath) ?? fullPath
            : fullPath;
    }

    private static string NormalizeResponseFilePath(string? filePath, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return string.Empty;

        var absolutePath = Path.IsPathRooted(filePath)
            ? Path.GetFullPath(filePath)
            : Path.GetFullPath(Path.Combine(workspaceRoot, filePath));
        return absolutePath.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(workspaceRoot, absolutePath)
            : absolutePath;
    }

    private sealed record SnapshotState(
        WorkspaceGraphSnapshot Snapshot,
        bool GraphBuiltDuringRequest);

    private sealed record SelectionResult(
        int TotalCandidateNodes,
        int TotalCandidateEdges,
        GraphProjectionNodeItem[] RenderedNodes,
        GraphProjectionEdgeItem[] RenderedEdges,
        string[] Warnings);

    public sealed record GraphProjectionResponse(
        string Summary,
        string Layout,
        string Mode,
        string WorkspaceRoot,
        string WorkspaceTargetPath,
        bool GraphBuiltDuringRequest,
        DateTimeOffset GraphBuiltAtUtc,
        string? FocusSymbol,
        string? ProjectFilter,
        bool IncludeTypes,
        bool IncludeMembers,
        bool IncludeDocuments,
        bool IncludeDi,
        bool IncludeEntrypoints,
        string[] EdgeKinds,
        bool WriteToFile,
        string? ExportPath,
        string Projection,
        GraphProjectionNodeItem[] Nodes,
        GraphProjectionEdgeItem[] Edges,
        int TotalCandidateNodes,
        int TotalCandidateEdges,
        int TruncatedNodes,
        int TruncatedEdges,
        string[] Warnings) : IStructuredToolResult;

    public sealed record GraphProjectionNodeItem(
        string GraphId,
        string NodeId,
        string Kind,
        string Label,
        string ProjectName,
        string RelativePath,
        int? Line,
        string? Reason);

    public sealed record GraphProjectionEdgeItem(
        string Kind,
        string SourceGraphId,
        string TargetGraphId,
        string SourceNodeId,
        string TargetNodeId);
}
