using CSharpLspMcp.Analysis.Architecture;
using CSharpLspMcp.Analysis.Testing;
using CSharpLspMcp.Contracts.Common;
using CSharpLspMcp.Storage.Graph;
using CSharpLspMcp.Workspace;

namespace CSharpLspMcp.Analysis.Graph;

public sealed class CSharpChangeImpactService
{
    private readonly CSharpEntrypointAnalysisService _entrypointAnalysisService;
    private readonly CSharpGraphBuildService _graphBuildService;
    private readonly CSharpGraphCacheStore _graphCacheStore;
    private readonly CSharpRegistrationAnalysisService _registrationAnalysisService;
    private readonly CSharpTestMapAnalysisService _testMapAnalysisService;
    private readonly CSharpWorkspaceSession _workspaceSession;
    private readonly WorkspaceState _workspaceState;

    public CSharpChangeImpactService(
        CSharpGraphBuildService graphBuildService,
        CSharpGraphCacheStore graphCacheStore,
        CSharpRegistrationAnalysisService registrationAnalysisService,
        CSharpEntrypointAnalysisService entrypointAnalysisService,
        CSharpTestMapAnalysisService testMapAnalysisService,
        CSharpWorkspaceSession workspaceSession,
        WorkspaceState workspaceState)
    {
        _graphBuildService = graphBuildService;
        _graphCacheStore = graphCacheStore;
        _registrationAnalysisService = registrationAnalysisService;
        _entrypointAnalysisService = entrypointAnalysisService;
        _testMapAnalysisService = testMapAnalysisService;
        _workspaceSession = workspaceSession;
        _workspaceState = workspaceState;
    }

    public async Task<ChangeImpactResponse> AnalyzeAsync(
        string? symbolQuery,
        string? filePath,
        bool includeTests,
        bool includeRegistrations,
        bool includeEntrypoints,
        bool rebuildIfMissing,
        int maxResults,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(symbolQuery) && string.IsNullOrWhiteSpace(filePath))
            throw new InvalidOperationException("Provide either symbolQuery or filePath.");

        var workspacePath = _workspaceState.CurrentPath;
        if (string.IsNullOrWhiteSpace(workspacePath))
            throw new InvalidOperationException("Workspace is not set. Call csharp_set_workspace first.");

        cancellationToken.ThrowIfCancellationRequested();

        var warnings = new List<string>();
        var effectiveMaxResults = Math.Max(1, maxResults);
        var snapshotState = await EnsureSnapshotAsync(workspacePath, includeTests, rebuildIfMissing, warnings, cancellationToken);
        var index = WorkspaceGraphIndex.Create(snapshotState.Snapshot);

        var targets = ResolveTargets(index, symbolQuery, filePath, workspacePath, warnings)
            .Take(Math.Max(3, effectiveMaxResults))
            .ToArray();
        var targetNodes = targets.Select(target => target.Node).ToArray();
        if (targetNodes.Length == 0)
        {
            return new ChangeImpactResponse(
                Summary: "No matching graph targets were found for the requested change.",
                WorkspaceRoot: snapshotState.Snapshot.WorkspaceRoot,
                GraphBuiltDuringRequest: snapshotState.GraphBuiltDuringRequest,
                GraphBuiltAtUtc: snapshotState.Snapshot.BuiltAtUtc,
                Query: symbolQuery,
                FilePath: NormalizeResponseFilePath(filePath, workspacePath),
                Targets: Array.Empty<ImpactTargetItem>(),
                IncomingCalls: Array.Empty<ImpactSymbolItem>(),
                RelatedSymbols: Array.Empty<ImpactSymbolItem>(),
                Registrations: Array.Empty<ImpactRegistrationItem>(),
                Entrypoints: Array.Empty<ImpactEntrypointItem>(),
                RelatedTests: Array.Empty<ImpactTestItem>(),
                RecommendedFiles: Array.Empty<RecommendedFileItem>(),
                Warnings: warnings.ToArray(),
                TruncatedTargets: 0,
                TruncatedIncomingCalls: 0,
                TruncatedRelatedSymbols: 0,
                TruncatedRegistrations: 0,
                TruncatedEntrypoints: 0,
                TruncatedTests: 0,
                TruncatedRecommendedFiles: 0);
        }

        var targetItems = targets
            .Take(effectiveMaxResults)
            .Select(target => MapTarget(target.MatchKind, target.Node, workspacePath))
            .ToArray();
        var callerSeedIds = targetNodes.SelectMany(node => ExpandCallSeedNodeIds(index, node)).Distinct(StringComparer.Ordinal).ToArray();
        var relationSeedIds = targetNodes.SelectMany(node => ExpandRelationshipSeedNodeIds(index, node)).Distinct(StringComparer.Ordinal).ToArray();
        var candidateNames = BuildCandidateNames(targetNodes);
        var projectNames = targetNodes
            .Select(node => node.ProjectName)
            .Where(projectName => !string.IsNullOrWhiteSpace(projectName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var incomingCallItems = BuildIncomingCallItems(index, callerSeedIds, workspacePath)
            .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Line ?? int.MaxValue)
            .Take(effectiveMaxResults)
            .ToArray();
        var totalIncomingCalls = BuildIncomingCallItems(index, callerSeedIds, workspacePath).Count;

        var relatedSymbolItems = BuildRelatedSymbolItems(index, relationSeedIds, workspacePath)
            .OrderBy(item => item.Relation, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Line ?? int.MaxValue)
            .Take(effectiveMaxResults)
            .ToArray();
        var totalRelatedSymbols = BuildRelatedSymbolItems(index, relationSeedIds, workspacePath).Count;

        var registrationItems = includeRegistrations
            ? await BuildRegistrationItemsAsync(candidateNames, projectNames, effectiveMaxResults, cancellationToken)
            : new CollectionSlice<ImpactRegistrationItem>(Array.Empty<ImpactRegistrationItem>(), 0);
        var entrypointItems = includeEntrypoints
            ? await BuildEntrypointItemsAsync(candidateNames, projectNames, effectiveMaxResults, cancellationToken)
            : new CollectionSlice<ImpactEntrypointItem>(Array.Empty<ImpactEntrypointItem>(), 0);
        var testItems = includeTests
            ? await BuildTestItemsAsync(symbolQuery, filePath, targetNodes, workspacePath, effectiveMaxResults, cancellationToken)
            : new CollectionSlice<ImpactTestItem>(Array.Empty<ImpactTestItem>(), 0);

        var recommendedFiles = BuildRecommendedFiles(
                workspacePath,
                targetItems,
                incomingCallItems,
                relatedSymbolItems,
                registrationItems.Items,
                entrypointItems.Items,
                testItems.Items)
            .Take(effectiveMaxResults)
            .ToArray();
        var totalRecommendedFiles = BuildRecommendedFiles(
                workspacePath,
                targetItems,
                incomingCallItems,
                relatedSymbolItems,
                registrationItems.Items,
                entrypointItems.Items,
                testItems.Items)
            .Count;

        var summary = $"Resolved {targetNodes.Length} target(s) with {totalIncomingCalls} incoming call site(s), {totalRelatedSymbols} inheritance or override relation(s), {registrationItems.TotalCount} DI registration match(es), {entrypointItems.TotalCount} entrypoint surface(s), and {testItems.TotalCount} related test candidate(s).";

        return new ChangeImpactResponse(
            Summary: summary,
            WorkspaceRoot: snapshotState.Snapshot.WorkspaceRoot,
            GraphBuiltDuringRequest: snapshotState.GraphBuiltDuringRequest,
            GraphBuiltAtUtc: snapshotState.Snapshot.BuiltAtUtc,
            Query: symbolQuery,
            FilePath: NormalizeResponseFilePath(filePath, workspacePath),
            Targets: targetItems,
            IncomingCalls: incomingCallItems,
            RelatedSymbols: relatedSymbolItems,
            Registrations: registrationItems.Items,
            Entrypoints: entrypointItems.Items,
            RelatedTests: testItems.Items,
            RecommendedFiles: recommendedFiles,
            Warnings: warnings
                .Concat(snapshotState.Snapshot.Warnings)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            TruncatedTargets: Math.Max(0, targetNodes.Length - effectiveMaxResults),
            TruncatedIncomingCalls: Math.Max(0, totalIncomingCalls - effectiveMaxResults),
            TruncatedRelatedSymbols: Math.Max(0, totalRelatedSymbols - effectiveMaxResults),
            TruncatedRegistrations: registrationItems.TruncatedCount,
            TruncatedEntrypoints: entrypointItems.TruncatedCount,
            TruncatedTests: testItems.TruncatedCount,
            TruncatedRecommendedFiles: Math.Max(0, totalRecommendedFiles - effectiveMaxResults));
    }

    private async Task<SnapshotState> EnsureSnapshotAsync(
        string workspacePath,
        bool includeTests,
        bool rebuildIfMissing,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var snapshot = await _graphCacheStore.LoadAsync(workspacePath, cancellationToken);
        var graphBuiltDuringRequest = false;

        if (snapshot == null || !HasCallGraphSupport(snapshot))
        {
            if (!rebuildIfMissing)
            {
                if (snapshot == null)
                    throw new InvalidOperationException("No persisted code graph is available. Run csharp_build_code_graph first or enable rebuildIfMissing.");

                warnings.Add("The persisted graph is missing call edges; rebuild was skipped because rebuildIfMissing=false.");
            }
            else
            {
                if (snapshot == null)
                    warnings.Add("No persisted graph was found. A new graph was built for impact analysis.");
                else
                    warnings.Add("The persisted graph was missing call edges. A fresh graph rebuild was performed.");

                await _graphBuildService.BuildAsync(
                    workspacePath,
                    mode: "incremental",
                    includeTests: includeTests,
                    includeGenerated: false,
                    cancellationToken);
                graphBuiltDuringRequest = true;
                snapshot = await _graphCacheStore.LoadAsync(workspacePath, cancellationToken);
            }
        }

        if (snapshot == null)
            throw new InvalidOperationException("No persisted code graph is available for the current workspace.");

        return new SnapshotState(snapshot, graphBuiltDuringRequest);
    }

    private static bool HasCallGraphSupport(WorkspaceGraphSnapshot snapshot)
        => (snapshot.Features?.Contains(WorkspaceGraphEdgeKinds.Calls, StringComparer.OrdinalIgnoreCase) ?? false) ||
           snapshot.EdgeCounts.Any(item =>
               string.Equals(item.Kind, WorkspaceGraphEdgeKinds.Calls, StringComparison.Ordinal) &&
               item.Count > 0);

    private IEnumerable<TargetMatch> ResolveTargets(
        WorkspaceGraphIndex index,
        string? symbolQuery,
        string? filePath,
        string workspacePath,
        ICollection<string> warnings)
    {
        var resolved = new Dictionary<string, TargetMatch>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(symbolQuery))
        {
            var symbolMatches = index.FindSymbolCandidates(symbolQuery, 12);
            foreach (var match in symbolMatches)
                resolved.TryAdd(match.Id, new TargetMatch("symbol", match));

            if (symbolMatches.Count == 0)
                warnings.Add($"No graph symbol matched '{symbolQuery}'.");
        }

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var absolutePath = _workspaceSession.GetAbsolutePath(filePath);
            var documentNode = index.GetDocument(absolutePath);
            if (documentNode == null)
            {
                warnings.Add($"No graph document matched '{NormalizeResponseFilePath(filePath, workspacePath)}'.");
            }
            else
            {
                resolved.TryAdd(documentNode.Id, new TargetMatch("document", documentNode));
                foreach (var declaredSymbol in index.GetDeclaredSymbolsInDocument(documentNode.Id))
                    resolved.TryAdd(declaredSymbol.Id, new TargetMatch("declared_symbol", declaredSymbol));
            }
        }

        return resolved.Values
            .OrderBy(match => match.MatchKind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(match => match.Node.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(match => match.Node.FilePath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(match => match.Node.Line ?? int.MaxValue);
    }

    private static IEnumerable<string> ExpandCallSeedNodeIds(WorkspaceGraphIndex index, WorkspaceGraphNode node)
    {
        if (string.Equals(node.Kind, WorkspaceGraphNodeKinds.Document, StringComparison.Ordinal))
        {
            foreach (var declaredSymbol in index.GetDeclaredSymbolsInDocument(node.Id))
                yield return declaredSymbol.Id;

            yield break;
        }

        yield return node.Id;

        if (!string.Equals(node.Kind, WorkspaceGraphNodeKinds.Type, StringComparison.Ordinal))
            yield break;

        foreach (var member in index.GetContainedNodes(node.Id)
                     .Where(member =>
                         string.Equals(member.Kind, WorkspaceGraphNodeKinds.Method, StringComparison.Ordinal) ||
                         string.Equals(member.Kind, WorkspaceGraphNodeKinds.Property, StringComparison.Ordinal) ||
                         string.Equals(member.Kind, WorkspaceGraphNodeKinds.Field, StringComparison.Ordinal) ||
                         string.Equals(member.Kind, WorkspaceGraphNodeKinds.Event, StringComparison.Ordinal)))
        {
            yield return member.Id;
        }
    }

    private static IEnumerable<string> ExpandRelationshipSeedNodeIds(WorkspaceGraphIndex index, WorkspaceGraphNode node)
    {
        if (string.Equals(node.Kind, WorkspaceGraphNodeKinds.Document, StringComparison.Ordinal))
        {
            foreach (var declaredSymbol in index.GetDeclaredSymbolsInDocument(node.Id))
                yield return declaredSymbol.Id;

            yield break;
        }

        yield return node.Id;
    }

    private static HashSet<string> BuildCandidateNames(IEnumerable<WorkspaceGraphNode> nodes)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            if (!string.IsNullOrWhiteSpace(node.DisplayName))
                candidates.Add(SanitizeCandidate(node.DisplayName));
            if (!string.IsNullOrWhiteSpace(node.MetadataName))
                candidates.Add(SanitizeCandidate(node.MetadataName));
            if (!string.IsNullOrWhiteSpace(node.DocumentationId))
            {
                var payload = ExtractDocumentationPayload(node.DocumentationId);
                candidates.Add(payload);
                candidates.Add(GetTrailingIdentifier(payload));
            }
        }

        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static List<ImpactSymbolItem> BuildIncomingCallItems(
        WorkspaceGraphIndex index,
        IEnumerable<string> seedNodeIds,
        string workspacePath)
    {
        return seedNodeIds
            .SelectMany(seedNodeId => index.GetIncomingSources(seedNodeId, WorkspaceGraphEdgeKinds.Calls))
            .DistinctBy(node => node.Id)
            .Select(node => MapSymbol("calls", node, workspacePath))
            .ToList();
    }

    private static List<ImpactSymbolItem> BuildRelatedSymbolItems(
        WorkspaceGraphIndex index,
        IEnumerable<string> seedNodeIds,
        string workspacePath)
    {
        var items = new List<ImpactSymbolItem>();
        foreach (var seedNodeId in seedNodeIds.Distinct(StringComparer.Ordinal))
        {
            items.AddRange(index.GetIncomingSources(seedNodeId, WorkspaceGraphEdgeKinds.Inherits)
                .DistinctBy(node => node.Id)
                .Select(node => MapSymbol("inherits", node, workspacePath)));
            items.AddRange(index.GetIncomingSources(seedNodeId, WorkspaceGraphEdgeKinds.Implements)
                .DistinctBy(node => node.Id)
                .Select(node => MapSymbol("implements", node, workspacePath)));
            items.AddRange(index.GetIncomingSources(seedNodeId, WorkspaceGraphEdgeKinds.Overrides)
                .DistinctBy(node => node.Id)
                .Select(node => MapSymbol("overrides", node, workspacePath)));
        }

        return items
            .DistinctBy(item => $"{item.Relation}|{item.DocumentationId}|{item.RelativePath}|{item.Line}")
            .ToList();
    }

    private async Task<CollectionSlice<ImpactRegistrationItem>> BuildRegistrationItemsAsync(
        IReadOnlySet<string> candidateNames,
        IReadOnlySet<string> projectNames,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var result = await _registrationAnalysisService.FindRegistrationsAsync(
            query: null,
            includeConsumers: true,
            maxResults: Math.Max(100, maxResults * 20),
            cancellationToken);

        var matches = result.Registrations
            .Where(registration => RegistrationMatches(registration, candidateNames, projectNames))
            .Select(registration => new ImpactRegistrationItem(
                registration.ServiceType,
                registration.ImplementationType,
                registration.Lifetime,
                registration.Project,
                registration.RelativePath,
                registration.LineNumber,
                registration.SourceText,
                registration.IsFactory,
                registration.IsEnumerable,
                registration.Consumers.Take(5)
                    .Select(consumer => new ImpactRegistrationConsumerItem(
                        consumer.Project,
                        consumer.RelativePath,
                        consumer.LineNumber,
                        consumer.DisplayText))
                    .ToArray()))
            .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.LineNumber)
            .ToArray();

        return new CollectionSlice<ImpactRegistrationItem>(
            matches.Take(maxResults).ToArray(),
            matches.Length);
    }

    private async Task<CollectionSlice<ImpactEntrypointItem>> BuildEntrypointItemsAsync(
        IReadOnlySet<string> candidateNames,
        IReadOnlySet<string> projectNames,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var result = await _entrypointAnalysisService.FindEntrypointsAsync(
            includeAspNetRoutes: true,
            includeHostedServices: true,
            includeMiddlewarePipeline: true,
            maxResults: Math.Max(100, maxResults * 20),
            cancellationToken);

        var items = new List<ImpactEntrypointItem>();
        items.AddRange(result.HostProjects
            .Where(project => projectNames.Contains(project.Name) ||
                              project.EndpointCompositionCalls.Any(call => candidateNames.Any(candidate => call.Contains(candidate, StringComparison.OrdinalIgnoreCase))))
            .Select(project => new ImpactEntrypointItem(
                "host_project",
                project.Name,
                project.ProgramPath ?? project.ProjectPath,
                null,
                $"{project.ProjectType} host")));
        items.AddRange(FilterSourceLocations("aspnet_route", result.AspNetRoutes, candidateNames));
        items.AddRange(FilterSourceLocations("hosted_service_registration", result.HostedServiceRegistrations, candidateNames));
        items.AddRange(FilterSourceLocations("background_service", result.BackgroundServiceImplementations, candidateNames));
        items.AddRange(FilterSourceLocations("serverless_handler", result.ServerlessHandlers, candidateNames));

        var orderedItems = items
            .DistinctBy(item => $"{item.Category}|{item.RelativePath}|{item.LineNumber}|{item.Text}")
            .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.LineNumber ?? int.MaxValue)
            .ToArray();

        return new CollectionSlice<ImpactEntrypointItem>(
            orderedItems.Take(maxResults).ToArray(),
            orderedItems.Length);
    }

    private async Task<CollectionSlice<ImpactTestItem>> BuildTestItemsAsync(
        string? symbolQuery,
        string? filePath,
        IReadOnlyList<WorkspaceGraphNode> targetNodes,
        string workspacePath,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var effectiveFilePath = filePath;
        if (string.IsNullOrWhiteSpace(effectiveFilePath))
        {
            effectiveFilePath = targetNodes
                .Select(node => node.FilePath)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        }

        var effectiveSymbolQuery = !string.IsNullOrWhiteSpace(symbolQuery)
            ? symbolQuery
            : targetNodes
                .Select(node => ExtractDocumentationPayload(node.DocumentationId))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        var result = await _testMapAnalysisService.GetTestMapAsync(
            effectiveFilePath,
            effectiveSymbolQuery,
            Math.Max(50, maxResults * 10),
            cancellationToken);

        var items = result.Matches
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(match => new ImpactTestItem(
                match.RelativePath,
                match.Score,
                match.Reasons,
                match.LineMatches.Take(3)
                    .Select(lineMatch => new ImpactTestLineItem(lineMatch.LineNumber, lineMatch.Text))
                    .ToArray()))
            .ToArray();

        return new CollectionSlice<ImpactTestItem>(
            items.Take(maxResults).ToArray(),
            items.Length);
    }

    private static IEnumerable<ImpactEntrypointItem> FilterSourceLocations(
        string category,
        IEnumerable<CSharpEntrypointAnalysisService.SourceLocationItem> sourceLocations,
        IReadOnlySet<string> candidateNames)
    {
        return sourceLocations
            .Where(location => candidateNames.Any(candidate => location.Text.Contains(candidate, StringComparison.OrdinalIgnoreCase)))
            .Select(location => new ImpactEntrypointItem(
                category,
                null,
                location.RelativePath,
                location.LineNumber,
                location.Text));
    }

    private static bool RegistrationMatches(
        CSharpRegistrationAnalysisService.RegistrationItem registration,
        IReadOnlySet<string> candidateNames,
        IReadOnlySet<string> projectNames)
    {
        if (projectNames.Contains(registration.Project) &&
            candidateNames.Any(candidate =>
                ContainsCandidate(registration.ServiceType, candidate) ||
                ContainsCandidate(registration.ImplementationType, candidate)))
        {
            return true;
        }

        if (candidateNames.Any(candidate =>
                ContainsCandidate(registration.ServiceType, candidate) ||
                ContainsCandidate(registration.ImplementationType, candidate)))
        {
            return true;
        }

        return registration.Consumers.Any(consumer =>
            projectNames.Contains(consumer.Project) &&
            candidateNames.Any(candidate => consumer.DisplayText.Contains(candidate, StringComparison.OrdinalIgnoreCase)));
    }

    private static IReadOnlyList<RecommendedFileItem> BuildRecommendedFiles(
        string workspacePath,
        IEnumerable<ImpactTargetItem> targets,
        IEnumerable<ImpactSymbolItem> incomingCalls,
        IEnumerable<ImpactSymbolItem> relatedSymbols,
        IEnumerable<ImpactRegistrationItem> registrations,
        IEnumerable<ImpactEntrypointItem> entrypoints,
        IEnumerable<ImpactTestItem> tests)
    {
        var scores = new Dictionary<string, RecommendedFileAccumulator>(StringComparer.OrdinalIgnoreCase);

        AddFiles(scores, targets.Select(target => (target.RelativePath, "target", 100)));
        AddFiles(scores, incomingCalls.Select(item => (item.RelativePath, $"caller:{item.DisplayName}", 80)));
        AddFiles(scores, relatedSymbols.Select(item => (item.RelativePath, $"{item.Relation}:{item.DisplayName}", 70)));
        AddFiles(scores, registrations.Select(item => (item.RelativePath, $"registration:{item.ServiceType}", 60)));
        AddFiles(scores, entrypoints.Select(item => (item.RelativePath, $"entrypoint:{item.Category}", 50)));
        AddFiles(scores, tests.Select(item => (item.RelativePath, "test", 40)));

        return scores.Values
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(item => new RecommendedFileItem(
                NormalizeResponseFilePath(item.RelativePath, workspacePath),
                item.Score,
                item.Reasons.OrderBy(reason => reason, StringComparer.OrdinalIgnoreCase).ToArray()))
            .ToArray();
    }

    private static void AddFiles(
        IDictionary<string, RecommendedFileAccumulator> scores,
        IEnumerable<(string RelativePath, string Reason, int Score)> items)
    {
        foreach (var (relativePath, reason, score) in items)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                continue;

            if (!scores.TryGetValue(relativePath, out var accumulator))
            {
                accumulator = new RecommendedFileAccumulator(relativePath);
                scores[relativePath] = accumulator;
            }

            accumulator.Score += score;
            accumulator.Reasons.Add(reason);
        }
    }

    private static ImpactTargetItem MapTarget(string matchKind, WorkspaceGraphNode node, string workspacePath)
        => new(
            MatchKind: matchKind,
            Kind: node.Kind,
            DisplayName: node.DisplayName,
            ProjectName: node.ProjectName,
            RelativePath: NormalizeResponseFilePath(node.FilePath, workspacePath),
            Line: node.Line,
            Character: node.Character,
            DocumentationId: node.DocumentationId);

    private static ImpactSymbolItem MapSymbol(string relation, WorkspaceGraphNode node, string workspacePath)
        => new(
            Relation: relation,
            Kind: node.Kind,
            DisplayName: node.DisplayName,
            ProjectName: node.ProjectName,
            RelativePath: NormalizeResponseFilePath(node.FilePath, workspacePath),
            Line: node.Line,
            Character: node.Character,
            DocumentationId: node.DocumentationId);

    private static string NormalizeResponseFilePath(string? filePath, string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return string.Empty;

        var absolutePath = Path.IsPathRooted(filePath)
            ? Path.GetFullPath(filePath)
            : Path.GetFullPath(Path.Combine(workspacePath, filePath));
        return absolutePath.StartsWith(workspacePath, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(workspacePath, absolutePath)
            : absolutePath;
    }

    private static bool ContainsCandidate(string? value, string candidate)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(candidate))
            return false;

        var simpleCandidate = GetTrailingIdentifier(candidate);
        return value.Contains(candidate, StringComparison.OrdinalIgnoreCase) ||
               value.Contains(simpleCandidate, StringComparison.OrdinalIgnoreCase);
    }

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

    private static string SanitizeCandidate(string value)
    {
        var trimmed = value.Trim();
        var parameterIndex = trimmed.IndexOf('(');
        return parameterIndex >= 0
            ? trimmed[..parameterIndex]
            : trimmed;
    }

    private sealed record SnapshotState(
        WorkspaceGraphSnapshot Snapshot,
        bool GraphBuiltDuringRequest);

    private sealed record TargetMatch(
        string MatchKind,
        WorkspaceGraphNode Node);

    private sealed class RecommendedFileAccumulator
    {
        public RecommendedFileAccumulator(string relativePath)
        {
            RelativePath = relativePath;
        }

        public string RelativePath { get; }

        public int Score { get; set; }

        public HashSet<string> Reasons { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record CollectionSlice<TItem>(
        TItem[] Items,
        int TotalCount)
    {
        public int TruncatedCount => Math.Max(0, TotalCount - Items.Length);
    }

    public sealed record ChangeImpactResponse(
        string Summary,
        string WorkspaceRoot,
        bool GraphBuiltDuringRequest,
        DateTimeOffset GraphBuiltAtUtc,
        string? Query,
        string FilePath,
        ImpactTargetItem[] Targets,
        ImpactSymbolItem[] IncomingCalls,
        ImpactSymbolItem[] RelatedSymbols,
        ImpactRegistrationItem[] Registrations,
        ImpactEntrypointItem[] Entrypoints,
        ImpactTestItem[] RelatedTests,
        RecommendedFileItem[] RecommendedFiles,
        string[] Warnings,
        int TruncatedTargets,
        int TruncatedIncomingCalls,
        int TruncatedRelatedSymbols,
        int TruncatedRegistrations,
        int TruncatedEntrypoints,
        int TruncatedTests,
        int TruncatedRecommendedFiles) : IStructuredToolResult;

    public sealed record ImpactTargetItem(
        string MatchKind,
        string Kind,
        string DisplayName,
        string ProjectName,
        string RelativePath,
        int? Line,
        int? Character,
        string? DocumentationId);

    public sealed record ImpactSymbolItem(
        string Relation,
        string Kind,
        string DisplayName,
        string ProjectName,
        string RelativePath,
        int? Line,
        int? Character,
        string? DocumentationId);

    public sealed record ImpactRegistrationItem(
        string ServiceType,
        string? ImplementationType,
        string Lifetime,
        string Project,
        string RelativePath,
        int LineNumber,
        string SourceText,
        bool IsFactory,
        bool IsEnumerable,
        ImpactRegistrationConsumerItem[] Consumers);

    public sealed record ImpactRegistrationConsumerItem(
        string Project,
        string RelativePath,
        int LineNumber,
        string DisplayText);

    public sealed record ImpactEntrypointItem(
        string Category,
        string? Name,
        string RelativePath,
        int? LineNumber,
        string Text);

    public sealed record ImpactTestItem(
        string RelativePath,
        int Score,
        string[] Reasons,
        ImpactTestLineItem[] LineMatches);

    public sealed record ImpactTestLineItem(
        int LineNumber,
        string Text);

    public sealed record RecommendedFileItem(
        string RelativePath,
        int Score,
        string[] Reasons);
}
