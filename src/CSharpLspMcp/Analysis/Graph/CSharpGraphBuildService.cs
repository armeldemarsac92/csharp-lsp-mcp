using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using CSharpLspMcp.Analysis.Architecture;
using CSharpLspMcp.Contracts.Common;
using CSharpLspMcp.Storage.Graph;
using CSharpLspMcp.Workspace;
using CSharpLspMcp.Workspace.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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

        using var roslynContext = await _roslynWorkspaceHost.OpenAsync(workspaceInput, cancellationToken);
        var warnings = roslynContext.Warnings.ToList();
        var normalizedMode = NormalizeMode(mode);
        var startedAt = DateTimeOffset.UtcNow;
        var buildResult = await BuildSnapshotAsync(
            roslynContext,
            normalizedMode,
            includeTests,
            includeGenerated,
            warnings,
            cancellationToken);

        await _graphCacheStore.SaveAsync(buildResult.Snapshot, cancellationToken);

        var snapshot = buildResult.Snapshot;
        var storagePath = _graphCacheStore.GetStoragePath(snapshot.WorkspaceRoot);
        var durationMs = Math.Max(0, (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);
        var summary = buildResult.IncrementalApplied
            ? $"Incrementally refreshed code graph for {snapshot.ProjectsIndexed} project(s); rebuilt {buildResult.RebuiltProjects.Length} project(s) and reused {buildResult.ReusedProjects.Length}."
            : $"Built code graph for {snapshot.ProjectsIndexed} project(s), {snapshot.DocumentsIndexed} document(s), and {snapshot.SymbolsIndexed} symbol node(s).";

        return new GraphBuildResponse(
            Summary: summary,
            SchemaVersion: snapshot.SchemaVersion,
            WorkspaceRoot: snapshot.WorkspaceRoot,
            WorkspaceTargetPath: snapshot.WorkspaceTargetPath,
            BuildMode: snapshot.BuildMode,
            IncludeTests: snapshot.IncludeTests,
            IncludeGenerated: snapshot.IncludeGenerated,
            IncrementalApplied: buildResult.IncrementalApplied,
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
            RebuiltProjects: buildResult.RebuiltProjects,
            ReusedProjects: buildResult.ReusedProjects,
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
                SchemaVersion: WorkspaceGraphSchema.CurrentVersion,
                WorkspaceRoot: workspaceRoot,
                WorkspaceTargetPath: null,
                BuildMode: null,
                IncludeTests: null,
                IncludeGenerated: null,
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
            SchemaVersion: snapshot.SchemaVersion,
            WorkspaceRoot: snapshot.WorkspaceRoot,
            WorkspaceTargetPath: snapshot.WorkspaceTargetPath,
            BuildMode: snapshot.BuildMode,
            IncludeTests: snapshot.IncludeTests,
            IncludeGenerated: snapshot.IncludeGenerated,
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

    private async Task<BuildSnapshotResult> BuildSnapshotAsync(
        RoslynWorkspaceContext roslynContext,
        string requestedMode,
        bool includeTests,
        bool includeGenerated,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var currentProjects = CreateProjectInputs(roslynContext.Solution, includeTests, includeGenerated);
        var currentProjectIds = currentProjects
            .Select(project => project.ProjectId)
            .ToHashSet(StringComparer.Ordinal);
        var solutionId = CreateSolutionId(roslynContext.WorkspaceRoot);

        WorkspaceGraphSnapshot? previousSnapshot = null;
        ReuseEligibility reuseEligibility = ReuseEligibility.Disabled("A full graph rebuild was requested.");
        if (string.Equals(requestedMode, "incremental", StringComparison.OrdinalIgnoreCase))
        {
            previousSnapshot = await _graphCacheStore.LoadAsync(roslynContext.WorkspaceRoot, cancellationToken);
            reuseEligibility = EvaluateReuseEligibility(
                previousSnapshot,
                roslynContext.TargetPath,
                includeTests,
                includeGenerated);
            if (!reuseEligibility.CanReuse && !string.IsNullOrWhiteSpace(reuseEligibility.Reason))
            {
                warnings.Add(reuseEligibility.Reason);
            }
        }

        var nodeMap = new Dictionary<string, WorkspaceGraphNode>(StringComparer.Ordinal);
        var edgeMap = new Dictionary<string, WorkspaceGraphEdge>(StringComparer.Ordinal);
        AddNode(
            nodeMap,
            new WorkspaceGraphNode(
                Id: solutionId,
                Kind: WorkspaceGraphNodeKinds.Solution,
                DisplayName: Path.GetFileName(roslynContext.WorkspaceRoot),
                ProjectName: string.Empty,
                OwningProjectId: null,
                FilePath: null,
                Line: null,
                Character: null,
                DocumentationId: null,
                MetadataName: null));

        HashSet<string> rebuiltProjectIds;
        HashSet<string> reusedProjectIds;
        if (reuseEligibility.CanReuse && previousSnapshot != null)
        {
            rebuiltProjectIds = DetermineStaleProjectIds(currentProjects, previousSnapshot);
            reusedProjectIds = currentProjects
                .Where(project => !rebuiltProjectIds.Contains(project.ProjectId))
                .Select(project => project.ProjectId)
                .ToHashSet(StringComparer.Ordinal);

            ReuseUnchangedProjectSlices(
                previousSnapshot,
                currentProjectIds,
                rebuiltProjectIds,
                solutionId,
                nodeMap,
                edgeMap);
        }
        else
        {
            rebuiltProjectIds = currentProjectIds;
            reusedProjectIds = new HashSet<string>(StringComparer.Ordinal);
        }

        var rebuiltProjects = currentProjects
            .Where(project => rebuiltProjectIds.Contains(project.ProjectId))
            .ToArray();

        foreach (var projectInput in rebuiltProjects)
        {
            await BuildProjectNodesAsync(
                projectInput,
                includeGenerated,
                nodeMap,
                edgeMap,
                warnings,
                cancellationToken);
        }

        foreach (var projectInput in rebuiltProjects)
        {
            await BuildCallEdgesForProjectAsync(
                projectInput.Project,
                includeGenerated,
                nodeMap,
                edgeMap,
                warnings,
                cancellationToken);
        }

        foreach (var projectInput in currentProjects)
        {
            AddEdge(edgeMap, WorkspaceGraphEdgeKinds.Contains, solutionId, projectInput.ProjectId);

            foreach (var referencedProjectId in projectInput.ReferencedProjectIds)
            {
                if (!currentProjectIds.Contains(referencedProjectId))
                    continue;

                AddEdge(
                    edgeMap,
                    WorkspaceGraphEdgeKinds.DependsOnProject,
                    projectInput.ProjectId,
                    referencedProjectId);
            }
        }

        BuildRegistrationGraph(
            roslynContext.WorkspaceRoot,
            currentProjects,
            nodeMap,
            edgeMap);
        BuildEntrypointGraph(
            roslynContext.WorkspaceRoot,
            currentProjects,
            nodeMap,
            edgeMap);

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

        var graphProjects = currentProjects
            .Select(project => new WorkspaceGraphProjectSummary(
                Id: project.ProjectId,
                Name: project.Name,
                FilePath: project.ProjectFilePath,
                AssemblyName: project.AssemblyName,
                TargetFrameworks: project.TargetFrameworks,
                IsTestProject: project.IsTestProject,
                DocumentsIndexed: graphNodes.Count(node =>
                    string.Equals(node.OwningProjectId, project.ProjectId, StringComparison.Ordinal) &&
                    string.Equals(node.Kind, WorkspaceGraphNodeKinds.Document, StringComparison.Ordinal)),
                SymbolsIndexed: graphNodes.Count(node =>
                    string.Equals(node.OwningProjectId, project.ProjectId, StringComparison.Ordinal) &&
                    !string.Equals(node.Kind, WorkspaceGraphNodeKinds.Project, StringComparison.Ordinal) &&
                    !string.Equals(node.Kind, WorkspaceGraphNodeKinds.Document, StringComparison.Ordinal)),
                ProjectReferenceCount: project.ReferencedProjectIds.Length))
            .ToArray();

        var snapshot = new WorkspaceGraphSnapshot(
            SchemaVersion: WorkspaceGraphSchema.CurrentVersion,
            WorkspaceRoot: roslynContext.WorkspaceRoot,
            WorkspaceTargetPath: roslynContext.TargetPath,
            BuiltAtUtc: DateTimeOffset.UtcNow,
            BuilderVersion: typeof(CSharpGraphBuildService).Assembly.GetName().Version?.ToString() ?? "1.0.0",
            BuildMode: reuseEligibility.CanReuse ? "incremental" : "full",
            IncludeTests: includeTests,
            IncludeGenerated: includeGenerated,
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
            ProjectStates: currentProjects
                .Select(project => new WorkspaceGraphProjectState(
                    ProjectId: project.ProjectId,
                    Name: project.Name,
                    FilePath: project.ProjectFilePath,
                    Fingerprint: project.Fingerprint,
                    ReferencedProjectIds: project.ReferencedProjectIds))
                .ToArray(),
            Nodes: graphNodes,
            Edges: graphEdges,
            Features: [
                WorkspaceGraphEdgeKinds.Calls,
                WorkspaceGraphEdgeKinds.RegisteredAs,
                WorkspaceGraphEdgeKinds.ConsumedBy,
                WorkspaceGraphNodeKinds.Entrypoint
            ],
            Warnings: warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

        return new BuildSnapshotResult(
            Snapshot: snapshot,
            IncrementalApplied: reuseEligibility.CanReuse,
            RebuiltProjects: currentProjects
                .Where(project => rebuiltProjectIds.Contains(project.ProjectId))
                .Select(project => project.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ReusedProjects: currentProjects
                .Where(project => reusedProjectIds.Contains(project.ProjectId))
                .Select(project => project.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private static ProjectGraphInput[] CreateProjectInputs(Solution solution, bool includeTests, bool includeGenerated)
    {
        var includedProjects = solution.Projects
            .Where(project => includeTests || !IsTestProject(project))
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var includedProjectIds = includedProjects
            .Select(project => project.Id)
            .ToHashSet();

        return includedProjects
            .Select(project =>
            {
                var projectId = CreateProjectId(project);
                var referencedProjectIds = project.ProjectReferences
                    .Where(reference => includedProjectIds.Contains(reference.ProjectId))
                    .Select(reference => solution.GetProject(reference.ProjectId))
                    .OfType<Project>()
                    .Select(CreateProjectId)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray();

                return new ProjectGraphInput(
                    Project: project,
                    ProjectId: projectId,
                    Name: project.Name,
                    ProjectFilePath: project.FilePath ?? project.Name,
                    AssemblyName: project.AssemblyName ?? project.Name,
                    IsTestProject: IsTestProject(project),
                    TargetFrameworks: GetTargetFrameworks(project),
                    Fingerprint: ComputeProjectFingerprint(project, includeGenerated),
                    ReferencedProjectIds: referencedProjectIds);
            })
            .ToArray();
    }

    private static ReuseEligibility EvaluateReuseEligibility(
        WorkspaceGraphSnapshot? previousSnapshot,
        string targetPath,
        bool includeTests,
        bool includeGenerated)
    {
        if (previousSnapshot == null)
            return ReuseEligibility.Disabled("No persisted graph snapshot was found; a full graph rebuild was performed.");

        if (previousSnapshot.SchemaVersion != WorkspaceGraphSchema.CurrentVersion)
            return ReuseEligibility.Disabled("The persisted graph schema is outdated; a full graph rebuild was performed.");

        if (!string.Equals(
                NormalizePath(previousSnapshot.WorkspaceTargetPath),
                NormalizePath(targetPath),
                StringComparison.Ordinal))
        {
            return ReuseEligibility.Disabled("The workspace target path changed; a full graph rebuild was performed.");
        }

        if (previousSnapshot.IncludeTests != includeTests || previousSnapshot.IncludeGenerated != includeGenerated)
        {
            return ReuseEligibility.Disabled("Graph build options changed; a full graph rebuild was performed.");
        }

        if (previousSnapshot.ProjectStates.Length == 0)
            return ReuseEligibility.Disabled("The persisted graph does not contain project fingerprints; a full graph rebuild was performed.");

        return ReuseEligibility.Enabled();
    }

    private static HashSet<string> DetermineStaleProjectIds(
        IReadOnlyCollection<ProjectGraphInput> currentProjects,
        WorkspaceGraphSnapshot previousSnapshot)
    {
        var previousStates = previousSnapshot.ProjectStates
            .ToDictionary(state => state.ProjectId, StringComparer.Ordinal);
        var staleProjectIds = currentProjects
            .Where(project =>
            {
                if (!previousStates.TryGetValue(project.ProjectId, out var previousState))
                    return true;

                return !string.Equals(previousState.Fingerprint, project.Fingerprint, StringComparison.Ordinal);
            })
            .Select(project => project.ProjectId)
            .ToHashSet(StringComparer.Ordinal);

        if (staleProjectIds.Count == 0)
            return staleProjectIds;

        var dependentsByProjectId = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var project in currentProjects)
        {
            foreach (var referencedProjectId in project.ReferencedProjectIds)
            {
                if (!dependentsByProjectId.TryGetValue(referencedProjectId, out var dependents))
                {
                    dependents = new HashSet<string>(StringComparer.Ordinal);
                    dependentsByProjectId[referencedProjectId] = dependents;
                }

                dependents.Add(project.ProjectId);
            }
        }

        var pending = new Queue<string>(staleProjectIds);
        while (pending.Count > 0)
        {
            var projectId = pending.Dequeue();
            if (!dependentsByProjectId.TryGetValue(projectId, out var dependents))
                continue;

            foreach (var dependentProjectId in dependents)
            {
                if (!staleProjectIds.Add(dependentProjectId))
                    continue;

                pending.Enqueue(dependentProjectId);
            }
        }

        return staleProjectIds;
    }

    private static void ReuseUnchangedProjectSlices(
        WorkspaceGraphSnapshot previousSnapshot,
        ISet<string> currentProjectIds,
        ISet<string> rebuiltProjectIds,
        string solutionId,
        IDictionary<string, WorkspaceGraphNode> nodeMap,
        IDictionary<string, WorkspaceGraphEdge> edgeMap)
    {
        var previousNodesById = previousSnapshot.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);

        foreach (var node in previousSnapshot.Nodes)
        {
            if (!IsReusableNode(node, currentProjectIds, rebuiltProjectIds))
                continue;

            AddNode(nodeMap, node);
        }

        foreach (var edge in previousSnapshot.Edges)
        {
            if (!IsReusableEdge(edge, previousNodesById, currentProjectIds, rebuiltProjectIds, solutionId))
                continue;

            AddEdge(edgeMap, edge.Kind, edge.SourceId, edge.TargetId);
        }
    }

    private static void BuildRegistrationGraph(
        string workspaceRoot,
        IReadOnlyCollection<ProjectGraphInput> currentProjects,
        IDictionary<string, WorkspaceGraphNode> nodeMap,
        IDictionary<string, WorkspaceGraphEdge> edgeMap)
    {
        var registrationResult = CSharpRegistrationAnalysisService.AnalyzeWorkspace(
            workspaceRoot,
            query: null,
            includeConsumers: true,
            maxResults: int.MaxValue);
        if (registrationResult.Registrations.Length == 0)
            return;

        var projectIdByName = currentProjects
            .ToDictionary(project => project.Name, project => project.ProjectId, StringComparer.OrdinalIgnoreCase);
        var nodes = nodeMap.Values.ToArray();

        foreach (var registration in registrationResult.Registrations)
        {
            if (!projectIdByName.TryGetValue(registration.Project, out var owningProjectId))
                continue;

            var absolutePath = NormalizePath(Path.Combine(workspaceRoot, registration.RelativePath));
            var registrationNodeId = CreateRegistrationNodeId(
                owningProjectId,
                registration.RelativePath,
                registration.LineNumber,
                registration.ServiceType,
                registration.ImplementationType);
            AddNode(
                nodeMap,
                new WorkspaceGraphNode(
                    Id: registrationNodeId,
                    Kind: WorkspaceGraphNodeKinds.DiRegistration,
                    DisplayName: GetRegistrationDisplayName(registration),
                    ProjectName: registration.Project,
                    OwningProjectId: owningProjectId,
                    FilePath: absolutePath,
                    Line: registration.LineNumber,
                    Character: 1,
                    DocumentationId: CreateRegistrationPayload(registration),
                    MetadataName: registration.SourceText));
            AddEdge(edgeMap, WorkspaceGraphEdgeKinds.Contains, owningProjectId, registrationNodeId);
            AddEdge(edgeMap, WorkspaceGraphEdgeKinds.DeclaredIn, registrationNodeId, CreateDocumentId(absolutePath));

            if (TryResolveTypeNodeId(registration.ServiceType, registration.Project, nodes, out var serviceNodeId))
            {
                AddEdge(edgeMap, WorkspaceGraphEdgeKinds.RegisteredAs, registrationNodeId, serviceNodeId);
            }

            if (!string.IsNullOrWhiteSpace(registration.ImplementationType) &&
                !string.Equals(registration.ImplementationType, "factory", StringComparison.OrdinalIgnoreCase) &&
                TryResolveTypeNodeId(registration.ImplementationType!, registration.Project, nodes, out var implementationNodeId))
            {
                AddEdge(edgeMap, WorkspaceGraphEdgeKinds.RegisteredAs, registrationNodeId, implementationNodeId);
            }

            foreach (var consumer in registration.Consumers)
            {
                var consumerFilePath = NormalizePath(Path.Combine(workspaceRoot, consumer.RelativePath));
                if (!TryResolveConsumerNodeId(consumer.Project, consumerFilePath, consumer.LineNumber, nodes, out var consumerNodeId))
                    continue;

                AddEdge(edgeMap, WorkspaceGraphEdgeKinds.ConsumedBy, registrationNodeId, consumerNodeId);
            }
        }
    }

    private static void BuildEntrypointGraph(
        string workspaceRoot,
        IReadOnlyCollection<ProjectGraphInput> currentProjects,
        IDictionary<string, WorkspaceGraphNode> nodeMap,
        IDictionary<string, WorkspaceGraphEdge> edgeMap)
    {
        var entrypointResult = CSharpEntrypointAnalysisService.AnalyzeWorkspace(
            workspaceRoot,
            includeAspNetRoutes: true,
            includeHostedServices: true,
            includeMiddlewarePipeline: true,
            maxResults: int.MaxValue);
        var projectIdByName = currentProjects
            .ToDictionary(project => project.Name, project => project.ProjectId, StringComparer.OrdinalIgnoreCase);

        foreach (var hostProject in entrypointResult.HostProjects)
        {
            if (!projectIdByName.TryGetValue(hostProject.Name, out var owningProjectId))
                continue;

            var absolutePath = !string.IsNullOrWhiteSpace(hostProject.ProgramPath)
                ? NormalizePath(Path.Combine(workspaceRoot, hostProject.ProgramPath))
                : NormalizePath(Path.Combine(workspaceRoot, hostProject.ProjectPath));
            var nodeId = CreateEntrypointNodeId("host_project", hostProject.ProjectPath, 1, hostProject.Name);
            AddNode(
                nodeMap,
                new WorkspaceGraphNode(
                    Id: nodeId,
                    Kind: WorkspaceGraphNodeKinds.Entrypoint,
                    DisplayName: hostProject.Name,
                    ProjectName: hostProject.Name,
                    OwningProjectId: owningProjectId,
                    FilePath: absolutePath,
                    Line: 1,
                    Character: 1,
                    DocumentationId: CreateEntrypointPayload("host_project", hostProject.Name, $"{hostProject.ProjectType} host"),
                    MetadataName: hostProject.ProjectType));
            AddEdge(edgeMap, WorkspaceGraphEdgeKinds.Contains, owningProjectId, nodeId);
            if (File.Exists(absolutePath))
            {
                AddEdge(edgeMap, WorkspaceGraphEdgeKinds.DeclaredIn, nodeId, CreateDocumentId(absolutePath));
            }
        }

        AddEntrypointSourceNodes("aspnet_route", entrypointResult.AspNetRoutes, workspaceRoot, projectIdByName, nodeMap, edgeMap);
        AddEntrypointSourceNodes("hosted_service_registration", entrypointResult.HostedServiceRegistrations, workspaceRoot, projectIdByName, nodeMap, edgeMap);
        AddEntrypointSourceNodes("background_service", entrypointResult.BackgroundServiceImplementations, workspaceRoot, projectIdByName, nodeMap, edgeMap);
        AddEntrypointSourceNodes("serverless_handler", entrypointResult.ServerlessHandlers, workspaceRoot, projectIdByName, nodeMap, edgeMap);
    }

    private static void AddEntrypointSourceNodes(
        string category,
        IEnumerable<CSharpEntrypointAnalysisService.SourceLocationItem> items,
        string workspaceRoot,
        IReadOnlyDictionary<string, string> projectIdByName,
        IDictionary<string, WorkspaceGraphNode> nodeMap,
        IDictionary<string, WorkspaceGraphEdge> edgeMap)
    {
        foreach (var item in items)
        {
            var absolutePath = NormalizePath(Path.Combine(workspaceRoot, item.RelativePath));
            var projectName = ResolveProjectName(absolutePath, projectIdByName.Keys);
            if (projectName == null || !projectIdByName.TryGetValue(projectName, out var owningProjectId))
                continue;

            var nodeId = CreateEntrypointNodeId(category, item.RelativePath, item.LineNumber, item.Text);
            AddNode(
                nodeMap,
                new WorkspaceGraphNode(
                    Id: nodeId,
                    Kind: WorkspaceGraphNodeKinds.Entrypoint,
                    DisplayName: item.Text,
                    ProjectName: projectName,
                    OwningProjectId: owningProjectId,
                    FilePath: absolutePath,
                    Line: item.LineNumber,
                    Character: 1,
                    DocumentationId: CreateEntrypointPayload(category, null, item.Text),
                    MetadataName: category));
            AddEdge(edgeMap, WorkspaceGraphEdgeKinds.Contains, owningProjectId, nodeId);
            AddEdge(edgeMap, WorkspaceGraphEdgeKinds.DeclaredIn, nodeId, CreateDocumentId(absolutePath));
        }
    }

    private static bool IsReusableNode(
        WorkspaceGraphNode node,
        ISet<string> currentProjectIds,
        ISet<string> rebuiltProjectIds)
    {
        if (string.Equals(node.Kind, WorkspaceGraphNodeKinds.Solution, StringComparison.Ordinal))
            return false;

        if (string.IsNullOrWhiteSpace(node.OwningProjectId))
            return false;

        return currentProjectIds.Contains(node.OwningProjectId) &&
            !rebuiltProjectIds.Contains(node.OwningProjectId);
    }

    private static bool IsReusableEdge(
        WorkspaceGraphEdge edge,
        IReadOnlyDictionary<string, WorkspaceGraphNode> previousNodesById,
        ISet<string> currentProjectIds,
        ISet<string> rebuiltProjectIds,
        string solutionId)
    {
        if (string.Equals(edge.Kind, WorkspaceGraphEdgeKinds.DependsOnProject, StringComparison.Ordinal))
            return false;

        if (string.Equals(edge.Kind, WorkspaceGraphEdgeKinds.Contains, StringComparison.Ordinal) &&
            string.Equals(edge.SourceId, solutionId, StringComparison.Ordinal))
        {
            return false;
        }

        var owningProjectId = ResolveEdgeOwningProjectId(edge, previousNodesById);
        if (string.IsNullOrWhiteSpace(owningProjectId))
            return false;

        return currentProjectIds.Contains(owningProjectId) &&
            !rebuiltProjectIds.Contains(owningProjectId);
    }

    private static string? ResolveEdgeOwningProjectId(
        WorkspaceGraphEdge edge,
        IReadOnlyDictionary<string, WorkspaceGraphNode> previousNodesById)
    {
        if (previousNodesById.TryGetValue(edge.SourceId, out var sourceNode) &&
            !string.IsNullOrWhiteSpace(sourceNode.OwningProjectId))
        {
            return sourceNode.OwningProjectId;
        }

        if (previousNodesById.TryGetValue(edge.TargetId, out var targetNode) &&
            !string.IsNullOrWhiteSpace(targetNode.OwningProjectId))
        {
            return targetNode.OwningProjectId;
        }

        return null;
    }

    private static async Task BuildProjectNodesAsync(
        ProjectGraphInput projectInput,
        bool includeGenerated,
        IDictionary<string, WorkspaceGraphNode> nodeMap,
        IDictionary<string, WorkspaceGraphEdge> edgeMap,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        AddNode(
            nodeMap,
            new WorkspaceGraphNode(
                Id: projectInput.ProjectId,
                Kind: WorkspaceGraphNodeKinds.Project,
                DisplayName: projectInput.Name,
                ProjectName: projectInput.Name,
                OwningProjectId: projectInput.ProjectId,
                FilePath: projectInput.ProjectFilePath,
                Line: null,
                Character: null,
                DocumentationId: null,
                MetadataName: projectInput.AssemblyName));

        foreach (var document in projectInput.Project.Documents
                     .Where(document => document.FilePath != null)
                     .Where(document => includeGenerated || !IsGeneratedPath(document.FilePath!))
                     .OrderBy(document => document.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            var documentId = CreateDocumentId(document.FilePath!);
            AddNode(
                nodeMap,
                new WorkspaceGraphNode(
                    Id: documentId,
                    Kind: WorkspaceGraphNodeKinds.Document,
                    DisplayName: Path.GetFileName(document.FilePath!),
                    ProjectName: projectInput.Name,
                    OwningProjectId: projectInput.ProjectId,
                    FilePath: document.FilePath,
                    Line: null,
                    Character: null,
                    DocumentationId: null,
                    MetadataName: null));
            AddEdge(edgeMap, WorkspaceGraphEdgeKinds.Contains, projectInput.ProjectId, documentId);
        }

        var compilation = await projectInput.Project.GetCompilationAsync(cancellationToken);
        if (compilation == null)
        {
            warnings.Add($"Failed to build compilation for project {projectInput.Name}.");
            return;
        }

        VisitNamespace(
            projectInput.Project,
            compilation.Assembly.GlobalNamespace,
            projectInput.ProjectId,
            includeGenerated,
            nodeMap,
            edgeMap,
            cancellationToken);
    }

    private static WorkspaceGraphCountItem[] BuildCounts(IEnumerable<string> kinds)
        => kinds
            .GroupBy(kind => kind, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new WorkspaceGraphCountItem(group.Key, group.Count()))
            .ToArray();

    private static async Task BuildCallEdgesForProjectAsync(
        Project project,
        bool includeGenerated,
        IDictionary<string, WorkspaceGraphNode> nodeMap,
        IDictionary<string, WorkspaceGraphEdge> edgeMap,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        foreach (var document in project.Documents
                     .Where(document => document.FilePath != null)
                     .Where(document => includeGenerated || !IsGeneratedPath(document.FilePath!))
                     .OrderBy(document => document.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (root == null || semanticModel == null)
            {
                warnings.Add($"Failed to build semantic model for document {document.FilePath}.");
                continue;
            }

            foreach (var sourceNode in root.DescendantNodes())
            {
                var referencedSymbol = TryResolveReferencedSymbol(sourceNode, semanticModel, cancellationToken);
                if (referencedSymbol == null)
                    continue;

                var sourceSymbol = ResolveGraphOwnerSymbol(semanticModel.GetEnclosingSymbol(sourceNode.SpanStart, cancellationToken));
                if (!TryResolveGraphNodeId(project, sourceSymbol, includeGenerated, nodeMap, out var sourceId))
                    continue;

                var targetSymbol = ResolveReferencedGraphSymbol(referencedSymbol);
                if (!TryResolveGraphNodeId(project, targetSymbol, includeGenerated, nodeMap, out var targetId))
                    continue;

                if (string.Equals(sourceId, targetId, StringComparison.Ordinal))
                    continue;

                AddEdge(edgeMap, WorkspaceGraphEdgeKinds.Calls, sourceId, targetId);
            }
        }
    }

    private static void VisitNamespace(
        Project project,
        INamespaceSymbol namespaceSymbol,
        string containerId,
        bool includeGenerated,
        IDictionary<string, WorkspaceGraphNode> nodeMap,
        IDictionary<string, WorkspaceGraphEdge> edgeMap,
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
            }
        }

        foreach (var member in namespaceSymbol.GetMembers().OrderBy(member => member.Name, StringComparer.OrdinalIgnoreCase))
        {
            switch (member)
            {
                case INamespaceSymbol childNamespace:
                    VisitNamespace(project, childNamespace, currentContainerId, includeGenerated, nodeMap, edgeMap, cancellationToken);
                    break;
                case INamedTypeSymbol namedType:
                    VisitNamedType(project, namedType, currentContainerId, includeGenerated, nodeMap, edgeMap, cancellationToken);
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
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryCreateSymbolNode(project, typeSymbol, WorkspaceGraphNodeKinds.Type, includeGenerated, out var typeNode))
            return;

        AddNode(nodeMap, typeNode);
        AddEdge(edgeMap, WorkspaceGraphEdgeKinds.Contains, containerId, typeNode.Id);
        AddDeclarationEdges(edgeMap, project, typeSymbol, includeGenerated, typeNode.Id);

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
                    VisitNamedType(project, nestedType, typeNode.Id, includeGenerated, nodeMap, edgeMap, cancellationToken);
                    break;
                case IMethodSymbol methodSymbol when ShouldIncludeMethod(methodSymbol):
                    VisitMember(project, methodSymbol, WorkspaceGraphNodeKinds.Method, typeNode.Id, includeGenerated, nodeMap, edgeMap);
                    break;
                case IPropertySymbol propertySymbol:
                    VisitMember(project, propertySymbol, WorkspaceGraphNodeKinds.Property, typeNode.Id, includeGenerated, nodeMap, edgeMap);
                    break;
                case IFieldSymbol fieldSymbol when !fieldSymbol.IsImplicitlyDeclared:
                    VisitMember(project, fieldSymbol, WorkspaceGraphNodeKinds.Field, typeNode.Id, includeGenerated, nodeMap, edgeMap);
                    break;
                case IEventSymbol eventSymbol:
                    VisitMember(project, eventSymbol, WorkspaceGraphNodeKinds.Event, typeNode.Id, includeGenerated, nodeMap, edgeMap);
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
        IDictionary<string, WorkspaceGraphEdge> edgeMap)
    {
        if (!TryCreateSymbolNode(project, symbol, nodeKind, includeGenerated, out var memberNode))
            return;

        AddNode(nodeMap, memberNode);
        AddEdge(edgeMap, WorkspaceGraphEdgeKinds.Contains, containerId, memberNode.Id);
        AddDeclarationEdges(edgeMap, project, symbol, includeGenerated, memberNode.Id);

        switch (symbol)
        {
            case IMethodSymbol methodSymbol:
                foreach (var interfaceMethod in GetImplementedInterfaceMembers(methodSymbol))
                {
                    AddEdge(edgeMap, WorkspaceGraphEdgeKinds.Implements, memberNode.Id, CreateSymbolId(project, interfaceMethod));
                }

                if (methodSymbol.OverriddenMethod != null)
                {
                    AddEdge(edgeMap, WorkspaceGraphEdgeKinds.Overrides, memberNode.Id, CreateSymbolId(project, methodSymbol.OverriddenMethod));
                }

                break;
            case IPropertySymbol propertySymbol:
                foreach (var interfaceProperty in GetImplementedInterfaceMembers(propertySymbol))
                {
                    AddEdge(edgeMap, WorkspaceGraphEdgeKinds.Implements, memberNode.Id, CreateSymbolId(project, interfaceProperty));
                }

                if (propertySymbol.OverriddenProperty != null)
                {
                    AddEdge(edgeMap, WorkspaceGraphEdgeKinds.Overrides, memberNode.Id, CreateSymbolId(project, propertySymbol.OverriddenProperty));
                }

                break;
            case IEventSymbol eventSymbol:
                foreach (var interfaceEvent in GetImplementedInterfaceMembers(eventSymbol))
                {
                    AddEdge(edgeMap, WorkspaceGraphEdgeKinds.Implements, memberNode.Id, CreateSymbolId(project, interfaceEvent));
                }

                if (eventSymbol.OverriddenEvent != null)
                {
                    AddEdge(edgeMap, WorkspaceGraphEdgeKinds.Overrides, memberNode.Id, CreateSymbolId(project, eventSymbol.OverriddenEvent));
                }

                break;
        }
    }

    private static IEnumerable<IMethodSymbol> GetImplementedInterfaceMembers(IMethodSymbol methodSymbol)
    {
        if (methodSymbol.MethodKind == MethodKind.Constructor || methodSymbol.ContainingType == null)
            return Array.Empty<IMethodSymbol>();

        return methodSymbol.ContainingType.AllInterfaces
            .SelectMany(interfaceSymbol => interfaceSymbol.GetMembers().OfType<IMethodSymbol>())
            .Where(interfaceMember =>
                SymbolEqualityComparer.Default.Equals(
                    methodSymbol.ContainingType.FindImplementationForInterfaceMember(interfaceMember)?.OriginalDefinition,
                    methodSymbol.OriginalDefinition))
            .DistinctBy(interfaceMember => interfaceMember.GetDocumentationCommentId() ?? interfaceMember.ToDisplayString());
    }

    private static IEnumerable<IPropertySymbol> GetImplementedInterfaceMembers(IPropertySymbol propertySymbol)
    {
        if (propertySymbol.ContainingType == null)
            return Array.Empty<IPropertySymbol>();

        return propertySymbol.ContainingType.AllInterfaces
            .SelectMany(interfaceSymbol => interfaceSymbol.GetMembers().OfType<IPropertySymbol>())
            .Where(interfaceMember =>
                SymbolEqualityComparer.Default.Equals(
                    propertySymbol.ContainingType.FindImplementationForInterfaceMember(interfaceMember)?.OriginalDefinition,
                    propertySymbol.OriginalDefinition))
            .DistinctBy(interfaceMember => interfaceMember.GetDocumentationCommentId() ?? interfaceMember.ToDisplayString());
    }

    private static IEnumerable<IEventSymbol> GetImplementedInterfaceMembers(IEventSymbol eventSymbol)
    {
        if (eventSymbol.ContainingType == null)
            return Array.Empty<IEventSymbol>();

        return eventSymbol.ContainingType.AllInterfaces
            .SelectMany(interfaceSymbol => interfaceSymbol.GetMembers().OfType<IEventSymbol>())
            .Where(interfaceMember =>
                SymbolEqualityComparer.Default.Equals(
                    eventSymbol.ContainingType.FindImplementationForInterfaceMember(interfaceMember)?.OriginalDefinition,
                    eventSymbol.OriginalDefinition))
            .DistinctBy(interfaceMember => interfaceMember.GetDocumentationCommentId() ?? interfaceMember.ToDisplayString());
    }

    private static ISymbol? TryResolveReferencedSymbol(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var symbolInfo = node switch
        {
            InvocationExpressionSyntax invocationExpression => semanticModel.GetSymbolInfo(invocationExpression, cancellationToken),
            ObjectCreationExpressionSyntax objectCreationExpression => semanticModel.GetSymbolInfo(objectCreationExpression, cancellationToken),
            ImplicitObjectCreationExpressionSyntax implicitObjectCreationExpression => semanticModel.GetSymbolInfo(implicitObjectCreationExpression, cancellationToken),
            ConstructorInitializerSyntax constructorInitializer => semanticModel.GetSymbolInfo(constructorInitializer, cancellationToken),
            _ => default
        };

        return symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
    }

    private static ISymbol? ResolveReferencedGraphSymbol(ISymbol? symbol)
        => symbol switch
        {
            IMethodSymbol methodSymbol when methodSymbol.ReducedFrom != null => methodSymbol.ReducedFrom,
            IMethodSymbol methodSymbol => methodSymbol,
            IPropertySymbol propertySymbol => propertySymbol,
            IFieldSymbol fieldSymbol when !fieldSymbol.IsImplicitlyDeclared => fieldSymbol,
            IEventSymbol eventSymbol => eventSymbol,
            INamedTypeSymbol namedTypeSymbol => namedTypeSymbol,
            _ => null
        };

    private static ISymbol? ResolveGraphOwnerSymbol(ISymbol? symbol)
    {
        var current = symbol;
        while (current != null)
        {
            switch (current)
            {
                case IMethodSymbol methodSymbol when ShouldIncludeMethod(methodSymbol):
                    return methodSymbol;
                case IPropertySymbol propertySymbol:
                    return propertySymbol;
                case IFieldSymbol fieldSymbol when !fieldSymbol.IsImplicitlyDeclared:
                    return fieldSymbol;
                case IEventSymbol eventSymbol:
                    return eventSymbol;
                case INamedTypeSymbol namedTypeSymbol:
                    return namedTypeSymbol;
                default:
                    current = current.ContainingSymbol;
                    break;
            }
        }

        return null;
    }

    private static bool TryResolveGraphNodeId(
        Project project,
        ISymbol? symbol,
        bool includeGenerated,
        IDictionary<string, WorkspaceGraphNode> nodeMap,
        out string nodeId)
    {
        nodeId = string.Empty;
        if (symbol == null || !HasIncludedSourceLocation(symbol, includeGenerated))
            return false;

        var candidateId = CreateSymbolId(project, symbol);
        if (!nodeMap.ContainsKey(candidateId))
            return false;

        nodeId = candidateId;
        return true;
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
        var projectId = CreateProjectId(project);
        node = new WorkspaceGraphNode(
            Id: CreateSymbolId(project, symbol),
            Kind: nodeKind,
            DisplayName: GetDisplayName(symbol),
            ProjectName: project.Name,
            OwningProjectId: projectId,
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

    private static string ComputeProjectFingerprint(Project project, bool includeGenerated)
    {
        var builder = new StringBuilder();
        builder.AppendLine(project.Name);
        builder.AppendLine(project.AssemblyName ?? string.Empty);
        AppendFileFingerprint(builder, project.FilePath);

        foreach (var projectReference in project.ProjectReferences
                     .Select(reference => project.Solution.GetProject(reference.ProjectId))
                     .OfType<Project>()
                     .Select(CreateProjectId)
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            builder.Append("ref:").AppendLine(projectReference);
        }

        foreach (var document in project.Documents
                     .Where(document => document.FilePath != null)
                     .Where(document => includeGenerated || !IsGeneratedPath(document.FilePath!))
                     .OrderBy(document => document.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            AppendFileFingerprint(builder, document.FilePath);
        }

        return ComputeSha256(builder.ToString());
    }

    private static void AppendFileFingerprint(StringBuilder builder, string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        var normalizedPath = NormalizePath(filePath);
        builder.Append("file:").Append(normalizedPath);
        if (!File.Exists(normalizedPath))
        {
            builder.AppendLine("|missing");
            return;
        }

        var fileInfo = new FileInfo(normalizedPath);
        builder.Append('|').Append(fileInfo.LastWriteTimeUtc.Ticks);
        builder.Append('|').Append(fileInfo.Length);
        builder.AppendLine();
    }

    private static string ComputeSha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string CreateRegistrationNodeId(
        string owningProjectId,
        string relativePath,
        int lineNumber,
        string serviceType,
        string? implementationType)
        => $"registration:{owningProjectId}:{relativePath.Replace('\\', '/')}:{lineNumber}:{serviceType}:{implementationType ?? string.Empty}";

    private static string GetRegistrationDisplayName(CSharpRegistrationAnalysisService.RegistrationItem registration)
        => string.IsNullOrWhiteSpace(registration.ImplementationType)
            ? $"{registration.Lifetime} {registration.ServiceType}"
            : $"{registration.Lifetime} {registration.ServiceType} -> {registration.ImplementationType}";

    private static string CreateRegistrationPayload(CSharpRegistrationAnalysisService.RegistrationItem registration)
        => string.Join(
            "|",
            [
                "di",
                registration.Lifetime,
                registration.ServiceType,
                registration.ImplementationType ?? string.Empty,
                registration.IsFactory ? "factory" : "direct",
                registration.IsEnumerable ? "enumerable" : "single"
            ]);

    private static string CreateEntrypointNodeId(string category, string relativePath, int lineNumber, string text)
        => $"entrypoint:{category}:{relativePath.Replace('\\', '/')}:{lineNumber}:{ComputeSha256(text)}";

    private static string CreateEntrypointPayload(string category, string? name, string text)
        => string.Join("|", ["entrypoint", category, name ?? string.Empty, text]);

    private static string? ResolveProjectName(string absolutePath, IEnumerable<string> projectNames)
    {
        var normalizedPath = NormalizePath(absolutePath);
        return projectNames
            .OrderByDescending(name => name.Length)
            .FirstOrDefault(name => normalizedPath.Contains($"/{name}/", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryResolveTypeNodeId(
        string typeName,
        string registrationProject,
        IEnumerable<WorkspaceGraphNode> nodes,
        out string nodeId)
    {
        nodeId = string.Empty;
        if (string.IsNullOrWhiteSpace(typeName))
            return false;

        var candidate = NormalizeTypeName(typeName);
        var simpleCandidate = GetSimpleTypeName(candidate);
        var match = nodes
            .Where(node => string.Equals(node.Kind, WorkspaceGraphNodeKinds.Type, StringComparison.Ordinal))
            .Select(node => new
            {
                Node = node,
                Score = GetTypeMatchScore(node, candidate, simpleCandidate, registrationProject)
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Node.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Node.FilePath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (match == null)
            return false;

        nodeId = match.Node.Id;
        return true;
    }

    private static int GetTypeMatchScore(
        WorkspaceGraphNode node,
        string candidate,
        string simpleCandidate,
        string registrationProject)
    {
        var score = 0;
        var documentationPayload = ExtractDocumentationPayload(node.DocumentationId);
        if (string.Equals(documentationPayload, candidate, StringComparison.OrdinalIgnoreCase))
            score = Math.Max(score, 300);
        else if (documentationPayload.EndsWith($".{candidate}", StringComparison.OrdinalIgnoreCase))
            score = Math.Max(score, 260);

        if (string.Equals(node.MetadataName, simpleCandidate, StringComparison.OrdinalIgnoreCase))
            score = Math.Max(score, 180);
        if (string.Equals(node.DisplayName, simpleCandidate, StringComparison.OrdinalIgnoreCase))
            score = Math.Max(score, 170);

        if (string.Equals(node.ProjectName, registrationProject, StringComparison.OrdinalIgnoreCase))
            score += 20;

        return score;
    }

    private static bool TryResolveConsumerNodeId(
        string consumerProject,
        string consumerFilePath,
        int lineNumber,
        IEnumerable<WorkspaceGraphNode> nodes,
        out string nodeId)
    {
        nodeId = string.Empty;
        var match = nodes
            .Where(node => !string.IsNullOrWhiteSpace(node.FilePath))
            .Where(node => !string.Equals(node.Kind, WorkspaceGraphNodeKinds.Project, StringComparison.Ordinal))
            .Where(node => !string.Equals(node.Kind, WorkspaceGraphNodeKinds.Document, StringComparison.Ordinal))
            .Where(node => !string.Equals(node.Kind, WorkspaceGraphNodeKinds.Namespace, StringComparison.Ordinal))
            .Where(node => string.Equals(NormalizePath(node.FilePath!), consumerFilePath, StringComparison.Ordinal))
            .Where(node => (node.Line ?? int.MaxValue) <= lineNumber)
            .Select(node => new
            {
                Node = node,
                KindScore = GetConsumerKindScore(node.Kind),
                LineDistance = lineNumber - (node.Line ?? lineNumber)
            })
            .OrderByDescending(item => string.Equals(item.Node.ProjectName, consumerProject, StringComparison.OrdinalIgnoreCase))
            .ThenBy(item => item.LineDistance)
            .ThenByDescending(item => item.KindScore)
            .FirstOrDefault();
        if (match == null)
            return false;

        nodeId = match.Node.Id;
        return true;
    }

    private static int GetConsumerKindScore(string kind)
        => kind switch
        {
            WorkspaceGraphNodeKinds.Method => 4,
            WorkspaceGraphNodeKinds.Property => 3,
            WorkspaceGraphNodeKinds.Event => 3,
            WorkspaceGraphNodeKinds.Type => 2,
            WorkspaceGraphNodeKinds.Field => 1,
            _ => 0
        };

    private static string ExtractDocumentationPayload(string? documentationId)
    {
        if (string.IsNullOrWhiteSpace(documentationId))
            return string.Empty;

        var trimmed = documentationId.Trim();
        var separatorIndex = trimmed.IndexOf(':');
        return separatorIndex >= 0 && separatorIndex < trimmed.Length - 1
            ? trimmed[(separatorIndex + 1)..]
            : trimmed;
    }

    private static string NormalizeTypeName(string value)
        => value
            .Replace("global::", string.Empty, StringComparison.Ordinal)
            .Trim()
            .TrimEnd('?');

    private static string GetSimpleTypeName(string value)
    {
        var normalizedValue = NormalizeTypeName(value);
        var lastDotIndex = normalizedValue.LastIndexOf('.');
        return lastDotIndex >= 0
            ? normalizedValue[(lastDotIndex + 1)..]
            : normalizedValue;
    }

    private static string GetDisplayName(ISymbol symbol)
        => symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

    private static void AddNode(IDictionary<string, WorkspaceGraphNode> nodeMap, WorkspaceGraphNode node)
    {
        nodeMap[node.Id] = node;
    }

    private static void AddEdge(
        IDictionary<string, WorkspaceGraphEdge> edgeMap,
        string kind,
        string sourceId,
        string targetId)
    {
        var key = $"{kind}|{sourceId}|{targetId}";
        edgeMap[key] = new WorkspaceGraphEdge(kind, sourceId, targetId);
    }

    private static string CreateSolutionId(string workspaceRoot)
        => $"solution:{NormalizePath(workspaceRoot)}";

    private static string CreateProjectId(Project project)
        => $"project:{NormalizePath(project.FilePath ?? project.Name)}";

    private static string CreateDocumentId(string filePath)
        => $"document:{NormalizePath(filePath)}";

    private static string CreateSymbolId(Project project, ISymbol symbol)
    {
        var assemblyName = symbol.ContainingAssembly?.Name ?? project.AssemblyName ?? project.Name;
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
        int SchemaVersion,
        string WorkspaceRoot,
        string WorkspaceTargetPath,
        string BuildMode,
        bool IncludeTests,
        bool IncludeGenerated,
        bool IncrementalApplied,
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
        string[] RebuiltProjects,
        string[] ReusedProjects,
        string[] Warnings,
        int DurationMs) : IStructuredToolResult;

    public sealed record GraphStatsResponse(
        string Summary,
        bool GraphAvailable,
        int SchemaVersion,
        string WorkspaceRoot,
        string? WorkspaceTargetPath,
        string? BuildMode,
        bool? IncludeTests,
        bool? IncludeGenerated,
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

    private sealed record ProjectGraphInput(
        Project Project,
        string ProjectId,
        string Name,
        string ProjectFilePath,
        string AssemblyName,
        bool IsTestProject,
        string[] TargetFrameworks,
        string Fingerprint,
        string[] ReferencedProjectIds);

    private sealed record ReuseEligibility(bool CanReuse, string? Reason)
    {
        public static ReuseEligibility Enabled()
            => new(true, null);

        public static ReuseEligibility Disabled(string reason)
            => new(false, reason);
    }

    private sealed record BuildSnapshotResult(
        WorkspaceGraphSnapshot Snapshot,
        bool IncrementalApplied,
        string[] RebuiltProjects,
        string[] ReusedProjects);
}
