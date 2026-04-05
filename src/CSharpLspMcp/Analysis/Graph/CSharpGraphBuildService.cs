using System.Xml.Linq;
using CSharpLspMcp.Contracts.Common;
using CSharpLspMcp.Storage.Graph;
using CSharpLspMcp.Workspace;
using CSharpLspMcp.Workspace.Roslyn;
using Microsoft.CodeAnalysis;

namespace CSharpLspMcp.Analysis.Graph;

public sealed class CSharpGraphBuildService
{
    private readonly CSharpGraphCacheStore _graphCacheStore;
    private readonly CSharpRoslynWorkspaceHost _roslynWorkspaceHost;
    private readonly WorkspaceState _workspaceState;

    public CSharpGraphBuildService(
        CSharpRoslynWorkspaceHost roslynWorkspaceHost,
        CSharpGraphCacheStore graphCacheStore,
        WorkspaceState workspaceState)
    {
        _roslynWorkspaceHost = roslynWorkspaceHost;
        _graphCacheStore = graphCacheStore;
        _workspaceState = workspaceState;
    }

    public async Task<GraphBuildResponse> BuildAsync(
        string? path,
        string mode,
        bool includeTests,
        bool includeGenerated,
        CancellationToken cancellationToken)
    {
        var workspaceInput = ResolveWorkspaceInput(path);
        var workspaceRoot = NormalizeWorkspaceRoot(workspaceInput);
        _workspaceState.SetPath(workspaceRoot);

        var normalizedMode = NormalizeMode(mode);
        using var roslynContext = await _roslynWorkspaceHost.OpenAsync(workspaceInput, cancellationToken);
        var warnings = roslynContext.Warnings.ToList();
        if (string.Equals(normalizedMode, "incremental", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("Incremental invalidation is not implemented yet; a full graph rebuild was performed.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var snapshot = await BuildSnapshotAsync(
            roslynContext,
            normalizedMode,
            includeTests,
            includeGenerated,
            warnings,
            cancellationToken);
        await _graphCacheStore.SaveAsync(snapshot, cancellationToken);
        var storagePath = _graphCacheStore.GetStoragePath(snapshot.WorkspaceRoot);
        var durationMs = Math.Max(0, (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);

        return new GraphBuildResponse(
            Summary: $"Built code graph for {snapshot.ProjectsIndexed} project(s), {snapshot.DocumentsIndexed} document(s), and {snapshot.SymbolsIndexed} symbol node(s).",
            WorkspaceRoot: snapshot.WorkspaceRoot,
            WorkspaceTargetPath: snapshot.WorkspaceTargetPath,
            BuildMode: snapshot.BuildMode,
            BuiltAtUtc: snapshot.BuiltAtUtc,
            BuilderVersion: snapshot.BuilderVersion,
            StoragePath: storagePath,
            ProjectsIndexed: snapshot.ProjectsIndexed,
            DocumentsIndexed: snapshot.DocumentsIndexed,
            SymbolsIndexed: snapshot.SymbolsIndexed,
            EdgesIndexed: snapshot.EdgesIndexed,
            NodeCounts: snapshot.NodeCounts,
            EdgeCounts: snapshot.EdgeCounts,
            Projects: snapshot.Projects,
            Warnings: snapshot.Warnings,
            DurationMs: durationMs);
    }

    public async Task<GraphStatsResponse> GetStatsAsync(string? path, CancellationToken cancellationToken)
    {
        var workspaceInput = ResolveWorkspaceInput(path);
        var workspaceRoot = NormalizeWorkspaceRoot(workspaceInput);
        var storagePath = _graphCacheStore.GetStoragePath(workspaceRoot);
        var snapshot = await _graphCacheStore.LoadAsync(workspaceRoot, cancellationToken);
        if (snapshot == null)
        {
            return new GraphStatsResponse(
                Summary: $"No persisted code graph found for {workspaceRoot}.",
                GraphAvailable: false,
                WorkspaceRoot: workspaceRoot,
                WorkspaceTargetPath: null,
                BuildMode: null,
                BuiltAtUtc: null,
                BuilderVersion: null,
                StoragePath: storagePath,
                ProjectsIndexed: 0,
                DocumentsIndexed: 0,
                SymbolsIndexed: 0,
                EdgesIndexed: 0,
                NodeCounts: Array.Empty<WorkspaceGraphCountItem>(),
                EdgeCounts: Array.Empty<WorkspaceGraphCountItem>(),
                Projects: Array.Empty<WorkspaceGraphProjectSummary>(),
                Warnings: Array.Empty<string>());
        }

        return new GraphStatsResponse(
            Summary: $"Loaded persisted code graph with {snapshot.ProjectsIndexed} project(s), {snapshot.DocumentsIndexed} document(s), and {snapshot.SymbolsIndexed} symbol node(s).",
            GraphAvailable: true,
            WorkspaceRoot: snapshot.WorkspaceRoot,
            WorkspaceTargetPath: snapshot.WorkspaceTargetPath,
            BuildMode: snapshot.BuildMode,
            BuiltAtUtc: snapshot.BuiltAtUtc,
            BuilderVersion: snapshot.BuilderVersion,
            StoragePath: storagePath,
            ProjectsIndexed: snapshot.ProjectsIndexed,
            DocumentsIndexed: snapshot.DocumentsIndexed,
            SymbolsIndexed: snapshot.SymbolsIndexed,
            EdgesIndexed: snapshot.EdgesIndexed,
            NodeCounts: snapshot.NodeCounts,
            EdgeCounts: snapshot.EdgeCounts,
            Projects: snapshot.Projects,
            Warnings: snapshot.Warnings);
    }

    private async Task<WorkspaceGraphSnapshot> BuildSnapshotAsync(
        RoslynWorkspaceContext roslynContext,
        string buildMode,
        bool includeTests,
        bool includeGenerated,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var solutionId = CreateSolutionId(roslynContext.WorkspaceRoot);
        var nodeMap = new Dictionary<string, WorkspaceGraphNode>(StringComparer.Ordinal);
        var edgeMap = new Dictionary<string, WorkspaceGraphEdge>(StringComparer.Ordinal);
        var projectSymbolIds = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var includedProjectIds = new HashSet<ProjectId>();

        AddNode(
            nodeMap,
            new WorkspaceGraphNode(
                solutionId,
                WorkspaceGraphNodeKinds.Solution,
                Path.GetFileName(roslynContext.WorkspaceRoot),
                string.Empty,
                null,
                null,
                null,
                null,
                null));

        var orderedProjects = roslynContext.Solution.Projects
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var project in orderedProjects)
        {
            if (!includeTests && IsTestProject(project))
                continue;

            includedProjectIds.Add(project.Id);
            projectSymbolIds[project.Name] = new HashSet<string>(StringComparer.Ordinal);
            AddNode(
                nodeMap,
                new WorkspaceGraphNode(
                    CreateProjectId(project),
                    WorkspaceGraphNodeKinds.Project,
                    project.Name,
                    project.Name,
                    project.FilePath,
                    null,
                    null,
                    null,
                    project.AssemblyName));
            AddEdge(edgeMap, WorkspaceGraphEdgeKinds.Contains, solutionId, CreateProjectId(project));

            foreach (var document in project.Documents
                         .Where(document => document.FilePath != null)
                         .Where(document => includeGenerated || !IsGeneratedPath(document.FilePath!))
                         .OrderBy(document => document.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                AddNode(
                    nodeMap,
                    new WorkspaceGraphNode(
                        CreateDocumentId(document.FilePath!),
                        WorkspaceGraphNodeKinds.Document,
                        Path.GetFileName(document.FilePath!),
                        project.Name,
                        document.FilePath,
                        null,
                        null,
                        null,
                        null));
                AddEdge(edgeMap, WorkspaceGraphEdgeKinds.Contains, CreateProjectId(project), CreateDocumentId(document.FilePath!));
            }
        }

        foreach (var project in orderedProjects)
        {
            if (!includedProjectIds.Contains(project.Id))
                continue;

            foreach (var projectReference in project.ProjectReferences)
            {
                if (!includedProjectIds.Contains(projectReference.ProjectId))
                    continue;

                var referencedProject = roslynContext.Solution.GetProject(projectReference.ProjectId);
                if (referencedProject == null)
                    continue;

                AddEdge(
                    edgeMap,
                    WorkspaceGraphEdgeKinds.DependsOnProject,
                    CreateProjectId(project),
                    CreateProjectId(referencedProject));
            }

            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null)
            {
                warnings.Add($"Failed to build compilation for project {project.Name}.");
                continue;
            }

            VisitNamespace(
                project,
                compilation.Assembly.GlobalNamespace,
                CreateProjectId(project),
                includeGenerated,
                nodeMap,
                edgeMap,
                projectSymbolIds[project.Name],
                cancellationToken);
        }

        var graphProjects = orderedProjects
            .Where(project => includedProjectIds.Contains(project.Id))
            .Select(project => new WorkspaceGraphProjectSummary(
                Name: project.Name,
                FilePath: project.FilePath ?? project.Name,
                AssemblyName: project.AssemblyName ?? project.Name,
                TargetFrameworks: GetTargetFrameworks(project),
                IsTestProject: IsTestProject(project),
                DocumentsIndexed: nodeMap.Values.Count(node =>
                    string.Equals(node.Kind, WorkspaceGraphNodeKinds.Document, StringComparison.Ordinal) &&
                    string.Equals(node.ProjectName, project.Name, StringComparison.Ordinal)),
                SymbolsIndexed: projectSymbolIds.TryGetValue(project.Name, out var symbolIds) ? symbolIds.Count : 0,
                ProjectReferenceCount: project.ProjectReferences.Count(reference => includedProjectIds.Contains(reference.ProjectId))))
            .ToArray();

        var graphNodes = nodeMap.Values
            .OrderBy(node => node.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.FilePath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var graphEdges = edgeMap.Values
            .OrderBy(edge => edge.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(edge => edge.SourceId, StringComparer.Ordinal)
            .ThenBy(edge => edge.TargetId, StringComparer.Ordinal)
            .ToArray();

        return new WorkspaceGraphSnapshot(
            WorkspaceRoot: roslynContext.WorkspaceRoot,
            WorkspaceTargetPath: roslynContext.TargetPath,
            BuiltAtUtc: DateTimeOffset.UtcNow,
            BuilderVersion: typeof(CSharpGraphBuildService).Assembly.GetName().Version?.ToString() ?? "1.0.0",
            BuildMode: buildMode,
            ProjectsIndexed: graphProjects.Length,
            DocumentsIndexed: graphNodes.Count(node => string.Equals(node.Kind, WorkspaceGraphNodeKinds.Document, StringComparison.Ordinal)),
            SymbolsIndexed: graphNodes.Count(node =>
                !string.Equals(node.Kind, WorkspaceGraphNodeKinds.Solution, StringComparison.Ordinal) &&
                !string.Equals(node.Kind, WorkspaceGraphNodeKinds.Project, StringComparison.Ordinal) &&
                !string.Equals(node.Kind, WorkspaceGraphNodeKinds.Document, StringComparison.Ordinal)),
            EdgesIndexed: graphEdges.Length,
            NodeCounts: BuildCounts(graphNodes.Select(node => node.Kind)),
            EdgeCounts: BuildCounts(graphEdges.Select(edge => edge.Kind)),
            Projects: graphProjects,
            Nodes: graphNodes,
            Edges: graphEdges,
            Warnings: warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static WorkspaceGraphCountItem[] BuildCounts(IEnumerable<string> kinds)
        => kinds
            .GroupBy(kind => kind, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new WorkspaceGraphCountItem(group.Key, group.Count()))
            .ToArray();

    private static void VisitNamespace(
        Project project,
        INamespaceSymbol namespaceSymbol,
        string containerId,
        bool includeGenerated,
        IDictionary<string, WorkspaceGraphNode> nodeMap,
        IDictionary<string, WorkspaceGraphEdge> edgeMap,
        ISet<string> projectSymbolIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var currentContainerId = containerId;
        if (!namespaceSymbol.IsGlobalNamespace && HasIncludedSourceLocation(namespaceSymbol, includeGenerated))
        {
            currentContainerId = CreateSymbolId(project, namespaceSymbol);
            if (TryCreateSymbolNode(project, namespaceSymbol, WorkspaceGraphNodeKinds.Namespace, includeGenerated, out var namespaceNode))
            {
                AddNode(nodeMap, namespaceNode);
                AddEdge(edgeMap, WorkspaceGraphEdgeKinds.Contains, containerId, namespaceNode.Id);
                AddDeclarationEdges(edgeMap, project, namespaceSymbol, includeGenerated, namespaceNode.Id);
                projectSymbolIds.Add(namespaceNode.Id);
            }
        }

        foreach (var member in namespaceSymbol.GetMembers().OrderBy(member => member.Name, StringComparer.OrdinalIgnoreCase))
        {
            switch (member)
            {
                case INamespaceSymbol childNamespace:
                    VisitNamespace(project, childNamespace, currentContainerId, includeGenerated, nodeMap, edgeMap, projectSymbolIds, cancellationToken);
                    break;
                case INamedTypeSymbol namedType:
                    VisitNamedType(project, namedType, currentContainerId, includeGenerated, nodeMap, edgeMap, projectSymbolIds, cancellationToken);
                    break;
            }
        }
    }

    private static void VisitNamedType(
        Project project,
        INamedTypeSymbol typeSymbol,
        string containerId,
        bool includeGenerated,
        IDictionary<string, WorkspaceGraphNode> nodeMap,
        IDictionary<string, WorkspaceGraphEdge> edgeMap,
        ISet<string> projectSymbolIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryCreateSymbolNode(project, typeSymbol, WorkspaceGraphNodeKinds.Type, includeGenerated, out var typeNode))
            return;

        AddNode(nodeMap, typeNode);
        AddEdge(edgeMap, WorkspaceGraphEdgeKinds.Contains, containerId, typeNode.Id);
        AddDeclarationEdges(edgeMap, project, typeSymbol, includeGenerated, typeNode.Id);
        projectSymbolIds.Add(typeNode.Id);

        if (typeSymbol.BaseType != null && !typeSymbol.BaseType.SpecialType.Equals(SpecialType.System_Object))
        {
            AddEdge(edgeMap, WorkspaceGraphEdgeKinds.Inherits, typeNode.Id, CreateSymbolId(project, typeSymbol.BaseType));
        }

        foreach (var interfaceSymbol in typeSymbol.Interfaces)
        {
            AddEdge(edgeMap, WorkspaceGraphEdgeKinds.Implements, typeNode.Id, CreateSymbolId(project, interfaceSymbol));
        }

        foreach (var member in typeSymbol.GetMembers().OrderBy(member => member.Name, StringComparer.OrdinalIgnoreCase))
        {
            switch (member)
            {
                case INamedTypeSymbol nestedType:
                    VisitNamedType(project, nestedType, typeNode.Id, includeGenerated, nodeMap, edgeMap, projectSymbolIds, cancellationToken);
                    break;
                case IMethodSymbol methodSymbol when ShouldIncludeMethod(methodSymbol):
                    VisitMember(project, methodSymbol, WorkspaceGraphNodeKinds.Method, typeNode.Id, includeGenerated, nodeMap, edgeMap, projectSymbolIds);
                    break;
                case IPropertySymbol propertySymbol:
                    VisitMember(project, propertySymbol, WorkspaceGraphNodeKinds.Property, typeNode.Id, includeGenerated, nodeMap, edgeMap, projectSymbolIds);
                    break;
                case IFieldSymbol fieldSymbol when !fieldSymbol.IsImplicitlyDeclared:
                    VisitMember(project, fieldSymbol, WorkspaceGraphNodeKinds.Field, typeNode.Id, includeGenerated, nodeMap, edgeMap, projectSymbolIds);
                    break;
                case IEventSymbol eventSymbol:
                    VisitMember(project, eventSymbol, WorkspaceGraphNodeKinds.Event, typeNode.Id, includeGenerated, nodeMap, edgeMap, projectSymbolIds);
                    break;
            }
        }
    }

    private static void VisitMember(
        Project project,
        ISymbol symbol,
        string nodeKind,
        string containerId,
        bool includeGenerated,
        IDictionary<string, WorkspaceGraphNode> nodeMap,
        IDictionary<string, WorkspaceGraphEdge> edgeMap,
        ISet<string> projectSymbolIds)
    {
        if (!TryCreateSymbolNode(project, symbol, nodeKind, includeGenerated, out var memberNode))
            return;

        AddNode(nodeMap, memberNode);
        AddEdge(edgeMap, WorkspaceGraphEdgeKinds.Contains, containerId, memberNode.Id);
        AddDeclarationEdges(edgeMap, project, symbol, includeGenerated, memberNode.Id);
        projectSymbolIds.Add(memberNode.Id);

        switch (symbol)
        {
            case IMethodSymbol methodSymbol when methodSymbol.OverriddenMethod != null:
                AddEdge(edgeMap, WorkspaceGraphEdgeKinds.Overrides, memberNode.Id, CreateSymbolId(project, methodSymbol.OverriddenMethod));
                break;
            case IPropertySymbol propertySymbol when propertySymbol.OverriddenProperty != null:
                AddEdge(edgeMap, WorkspaceGraphEdgeKinds.Overrides, memberNode.Id, CreateSymbolId(project, propertySymbol.OverriddenProperty));
                break;
            case IEventSymbol eventSymbol when eventSymbol.OverriddenEvent != null:
                AddEdge(edgeMap, WorkspaceGraphEdgeKinds.Overrides, memberNode.Id, CreateSymbolId(project, eventSymbol.OverriddenEvent));
                break;
        }
    }

    private static void AddDeclarationEdges(
        IDictionary<string, WorkspaceGraphEdge> edgeMap,
        Project project,
        ISymbol symbol,
        bool includeGenerated,
        string sourceNodeId)
    {
        foreach (var location in GetIncludedSourceLocations(symbol, includeGenerated))
        {
            AddEdge(edgeMap, WorkspaceGraphEdgeKinds.DeclaredIn, sourceNodeId, CreateDocumentId(location.SourceTree!.FilePath!));
        }
    }

    private static bool TryCreateSymbolNode(
        Project project,
        ISymbol symbol,
        string nodeKind,
        bool includeGenerated,
        out WorkspaceGraphNode node)
    {
        node = default!;
        var sourceLocation = GetIncludedSourceLocations(symbol, includeGenerated).FirstOrDefault();
        if (sourceLocation == null)
            return false;

        var lineSpan = sourceLocation.GetLineSpan();
        var documentationId = symbol.GetDocumentationCommentId();
        node = new WorkspaceGraphNode(
            Id: CreateSymbolId(project, symbol),
            Kind: nodeKind,
            DisplayName: GetDisplayName(symbol),
            ProjectName: project.Name,
            FilePath: sourceLocation.SourceTree?.FilePath,
            Line: lineSpan.StartLinePosition.Line + 1,
            Character: lineSpan.StartLinePosition.Character + 1,
            DocumentationId: documentationId,
            MetadataName: symbol.MetadataName);
        return true;
    }

    private static bool ShouldIncludeMethod(IMethodSymbol methodSymbol)
    {
        if (methodSymbol.IsImplicitlyDeclared)
            return false;

        return methodSymbol.MethodKind is MethodKind.Ordinary or
            MethodKind.Constructor or
            MethodKind.StaticConstructor or
            MethodKind.Conversion or
            MethodKind.UserDefinedOperator or
            MethodKind.DelegateInvoke;
    }

    private static bool HasIncludedSourceLocation(ISymbol symbol, bool includeGenerated)
        => GetIncludedSourceLocations(symbol, includeGenerated).Length > 0;

    private static Location[] GetIncludedSourceLocations(ISymbol symbol, bool includeGenerated)
    {
        return symbol.Locations
            .Where(location => location.IsInSource && location.SourceTree?.FilePath != null)
            .Where(location => includeGenerated || !IsGeneratedPath(location.SourceTree!.FilePath!))
            .DistinctBy(location => $"{location.SourceTree!.FilePath}|{location.SourceSpan.Start}")
            .ToArray();
    }

    private static bool IsGeneratedPath(string filePath)
    {
        if (filePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
            filePath.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) ||
            filePath.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase) ||
            filePath.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
            filePath.EndsWith(".AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var segments = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment =>
            string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(segment, "Generated", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTestProject(Project project)
    {
        if (project.Name.Contains("Test", StringComparison.OrdinalIgnoreCase))
            return true;

        var filePath = project.FilePath;
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var segments = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment =>
            string.Equals(segment, "test", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(segment, "tests", StringComparison.OrdinalIgnoreCase));
    }

    private static string[] GetTargetFrameworks(Project project)
    {
        if (string.IsNullOrWhiteSpace(project.FilePath) || !File.Exists(project.FilePath))
            return Array.Empty<string>();

        try
        {
            var document = XDocument.Load(project.FilePath);
            var root = document.Root;
            if (root == null)
                return Array.Empty<string>();

            XNamespace ns = root.Name.Namespace;
            var targetFramework = document.Descendants(ns + "TargetFramework")
                .Select(element => element.Value.Trim());
            var targetFrameworks = document.Descendants(ns + "TargetFrameworks")
                .SelectMany(element => element.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            return targetFramework
                .Concat(targetFrameworks)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string GetDisplayName(ISymbol symbol)
        => symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

    private static void AddNode(IDictionary<string, WorkspaceGraphNode> nodeMap, WorkspaceGraphNode node)
    {
        nodeMap.TryAdd(node.Id, node);
    }

    private static void AddEdge(
        IDictionary<string, WorkspaceGraphEdge> edgeMap,
        string kind,
        string sourceId,
        string targetId)
    {
        var key = $"{kind}|{sourceId}|{targetId}";
        edgeMap.TryAdd(key, new WorkspaceGraphEdge(kind, sourceId, targetId));
    }

    private static string CreateSolutionId(string workspaceRoot)
        => $"solution:{NormalizePath(workspaceRoot)}";

    private static string CreateProjectId(Project project)
        => $"project:{NormalizePath(project.FilePath ?? project.Name)}";

    private static string CreateDocumentId(string filePath)
        => $"document:{NormalizePath(filePath)}";

    private static string CreateSymbolId(Project project, ISymbol symbol)
    {
        var assemblyName = project.AssemblyName ?? project.Name;
        var documentationId = symbol.GetDocumentationCommentId();
        if (!string.IsNullOrWhiteSpace(documentationId))
            return $"symbol:{assemblyName}::{documentationId}";

        var firstLocation = symbol.Locations.FirstOrDefault(location => location.IsInSource && location.SourceTree?.FilePath != null);
        if (firstLocation?.SourceTree?.FilePath != null)
        {
            var lineSpan = firstLocation.GetLineSpan();
            return $"symbol:{assemblyName}::{NormalizePath(firstLocation.SourceTree.FilePath)}:{lineSpan.StartLinePosition.Line}:{lineSpan.StartLinePosition.Character}:{symbol.MetadataName}";
        }

        return $"symbol:{assemblyName}::{symbol.Kind}:{symbol.MetadataName}";
    }

    private static string NormalizePath(string path)
        => Path.GetFullPath(path).Replace('\\', '/');

    private string ResolveWorkspaceInput(string? path)
    {
        var candidate = string.IsNullOrWhiteSpace(path)
            ? _workspaceState.CurrentPath
            : path;
        if (string.IsNullOrWhiteSpace(candidate))
            throw new InvalidOperationException("Workspace is not set. Call csharp_set_workspace first or provide a path.");

        return Path.GetFullPath(candidate);
    }

    private static string NormalizeMode(string? mode)
        => string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase)
            ? "full"
            : "incremental";

    private static string NormalizeWorkspaceRoot(string workspaceInput)
    {
        var fullPath = Path.GetFullPath(workspaceInput);
        return File.Exists(fullPath)
            ? Path.GetDirectoryName(fullPath) ?? fullPath
            : fullPath;
    }

    public sealed record GraphBuildResponse(
        string Summary,
        string WorkspaceRoot,
        string WorkspaceTargetPath,
        string BuildMode,
        DateTimeOffset BuiltAtUtc,
        string BuilderVersion,
        string StoragePath,
        int ProjectsIndexed,
        int DocumentsIndexed,
        int SymbolsIndexed,
        int EdgesIndexed,
        WorkspaceGraphCountItem[] NodeCounts,
        WorkspaceGraphCountItem[] EdgeCounts,
        WorkspaceGraphProjectSummary[] Projects,
        string[] Warnings,
        int DurationMs) : IStructuredToolResult;

    public sealed record GraphStatsResponse(
        string Summary,
        bool GraphAvailable,
        string WorkspaceRoot,
        string? WorkspaceTargetPath,
        string? BuildMode,
        DateTimeOffset? BuiltAtUtc,
        string? BuilderVersion,
        string StoragePath,
        int ProjectsIndexed,
        int DocumentsIndexed,
        int SymbolsIndexed,
        int EdgesIndexed,
        WorkspaceGraphCountItem[] NodeCounts,
        WorkspaceGraphCountItem[] EdgeCounts,
        WorkspaceGraphProjectSummary[] Projects,
        string[] Warnings) : IStructuredToolResult;
}
