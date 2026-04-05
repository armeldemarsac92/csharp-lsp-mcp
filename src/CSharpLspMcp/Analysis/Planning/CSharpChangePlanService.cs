using CSharpLspMcp.Analysis.Architecture;
using CSharpLspMcp.Analysis.Graph;
using CSharpLspMcp.Contracts.Common;
using CSharpLspMcp.Workspace;

namespace CSharpLspMcp.Analysis.Planning;

public sealed class CSharpChangePlanService
{
    private readonly CSharpChangeImpactService _changeImpactService;
    private readonly CSharpProjectOverviewAnalysisService _projectOverviewAnalysisService;
    private readonly WorkspaceState _workspaceState;

    public CSharpChangePlanService(
        CSharpChangeImpactService changeImpactService,
        CSharpProjectOverviewAnalysisService projectOverviewAnalysisService,
        WorkspaceState workspaceState)
    {
        _changeImpactService = changeImpactService;
        _projectOverviewAnalysisService = projectOverviewAnalysisService;
        _workspaceState = workspaceState;
    }

    public async Task<ChangePlanResponse> PlanAsync(
        string? request,
        string? symbolQuery,
        string? filePath,
        bool includeTests,
        bool rebuildIfMissing,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var workspacePath = _workspaceState.CurrentPath;
        if (string.IsNullOrWhiteSpace(workspacePath))
            throw new InvalidOperationException("Workspace is not set. Call csharp_set_workspace first.");

        var effectiveMaxResults = Math.Max(1, maxResults);
        var impact = await _changeImpactService.AnalyzeAsync(
            symbolQuery,
            filePath,
            includeTests,
            includeRegistrations: true,
            includeEntrypoints: true,
            rebuildIfMissing,
            Math.Max(20, effectiveMaxResults * 3),
            cancellationToken);
        var overview = await _projectOverviewAnalysisService.GetProjectOverviewAsync(
            maxProjects: 250,
            maxPackagesPerProject: 6,
            maxProjectReferencesPerProject: 12,
            cancellationToken);

        var primaryTargets = SelectPrimaryTargets(impact.Targets)
            .Take(effectiveMaxResults)
            .ToArray();
        var projectIndex = overview.Projects
            .ToDictionary(project => project.Name, StringComparer.OrdinalIgnoreCase);
        var projectDirectoryIndex = overview.Projects
            .Where(project => !string.IsNullOrWhiteSpace(project.Path))
            .Select(project => new ProjectDirectoryItem(
                project.Name,
                GetProjectDirectory(project.Path)))
            .OrderByDescending(project => project.Directory.Length)
            .ToArray();

        var impactedProjectNames = BuildImpactedProjectNames(impact, projectDirectoryIndex);
        var impactedProjects = impactedProjectNames
            .Select(projectName => projectIndex.GetValueOrDefault(projectName))
            .OfType<CSharpProjectOverviewAnalysisService.ProjectOverviewItem>()
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var runtimeSurfaceFiles = impact.Registrations
            .Select(item => item.RelativePath)
            .Concat(impact.Entrypoints.Select(item => item.RelativePath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var relatedTestProjects = ResolveRelatedTestProjects(impact.RelatedTests, overview.Projects, projectDirectoryIndex)
            .Take(effectiveMaxResults)
            .ToArray();

        var inspectionSteps = BuildInspectionSteps(primaryTargets, impact, runtimeSurfaceFiles, relatedTestProjects, effectiveMaxResults);
        var editSteps = BuildEditSteps(primaryTargets, impact, runtimeSurfaceFiles, effectiveMaxResults);
        var verificationSteps = BuildVerificationSteps(workspacePath, overview, impactedProjects, relatedTestProjects, impact, effectiveMaxResults);
        var suggestedCommands = verificationSteps
            .Select(step => step.Command)
            .Where(command => !string.IsNullOrWhiteSpace(command))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new ChangePlanResponse(
            Summary: BuildSummary(request, primaryTargets.Length, impact, verificationSteps.Length),
            WorkspaceRoot: workspacePath,
            Request: request,
            Query: symbolQuery,
            FilePath: string.IsNullOrWhiteSpace(filePath) ? string.Empty : filePath,
            GraphBuiltDuringRequest: impact.GraphBuiltDuringRequest,
            GraphBuiltAtUtc: impact.GraphBuiltAtUtc,
            PrimaryTargets: primaryTargets,
            ImpactedProjects: impactedProjects
                .Take(effectiveMaxResults)
                .Select(project => new PlannedProjectItem(
                    project.Name,
                    project.Path,
                    project.ProjectType,
                    project.IsTestProject,
                    project.Entrypoints))
                .ToArray(),
            InspectionSteps: inspectionSteps,
            EditSteps: editSteps,
            VerificationSteps: verificationSteps,
            SuggestedCommands: suggestedCommands,
            RecommendedFiles: impact.RecommendedFiles.Take(effectiveMaxResults).ToArray(),
            Warnings: impact.Warnings,
            TruncatedPrimaryTargets: Math.Max(0, SelectPrimaryTargets(impact.Targets).Count - effectiveMaxResults),
            TruncatedImpactedProjects: Math.Max(0, impactedProjects.Length - effectiveMaxResults),
            TruncatedInspectionSteps: Math.Max(0, BuildInspectionSteps(primaryTargets, impact, runtimeSurfaceFiles, relatedTestProjects, int.MaxValue).Length - inspectionSteps.Length),
            TruncatedEditSteps: Math.Max(0, BuildEditSteps(primaryTargets, impact, runtimeSurfaceFiles, int.MaxValue).Length - editSteps.Length),
            TruncatedVerificationSteps: Math.Max(0, BuildVerificationSteps(workspacePath, overview, impactedProjects, relatedTestProjects, impact, int.MaxValue).Length - verificationSteps.Length));
    }

    private static string BuildSummary(
        string? request,
        int targetCount,
        CSharpChangeImpactService.ChangeImpactResponse impact,
        int verificationStepCount)
    {
        var requestPrefix = string.IsNullOrWhiteSpace(request)
            ? string.Empty
            : $"Planned '{request}' across ";
        return $"{requestPrefix}{targetCount} primary target(s), {impact.IncomingCalls.Length} direct caller(s), {impact.RelatedTests.Length} related test candidate(s), and {verificationStepCount} verification step(s).";
    }

    private static IReadOnlyList<CSharpChangeImpactService.ImpactTargetItem> SelectPrimaryTargets(
        IReadOnlyCollection<CSharpChangeImpactService.ImpactTargetItem> targets)
    {
        var orderedTargets = targets
            .OrderBy(target => GetTargetPriority(target))
            .ThenBy(target => target.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(target => target.Line ?? int.MaxValue)
            .ToArray();
        var nonTestTargets = orderedTargets
            .Where(target => !LooksLikeTestPath(target.RelativePath) && !LooksLikeTestProject(target.ProjectName))
            .ToArray();
        return nonTestTargets.Length > 0 ? nonTestTargets : orderedTargets;
    }

    private static int GetTargetPriority(CSharpChangeImpactService.ImpactTargetItem target)
        => target.MatchKind switch
        {
            "symbol" => 0,
            "declared_symbol" => 1,
            "document" => 2,
            _ => 3
        };

    private static string[] BuildImpactedProjectNames(
        CSharpChangeImpactService.ChangeImpactResponse impact,
        IReadOnlyCollection<ProjectDirectoryItem> projectDirectoryIndex)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectName in impact.Targets.Select(item => item.ProjectName)
                     .Concat(impact.IncomingCalls.Select(item => item.ProjectName))
                     .Concat(impact.RelatedSymbols.Select(item => item.ProjectName))
                     .Concat(impact.Registrations.Select(item => item.Project))
                     .Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            names.Add(projectName);
        }

        foreach (var path in impact.RelatedTests.Select(item => item.RelativePath))
        {
            var projectName = ResolveProjectNameForPath(path, projectDirectoryIndex);
            if (!string.IsNullOrWhiteSpace(projectName))
                names.Add(projectName);
        }

        foreach (var entrypoint in impact.Entrypoints)
        {
            if (!string.IsNullOrWhiteSpace(entrypoint.Name))
                names.Add(entrypoint.Name);

            var projectName = ResolveProjectNameForPath(entrypoint.RelativePath, projectDirectoryIndex);
            if (!string.IsNullOrWhiteSpace(projectName))
                names.Add(projectName);
        }

        return names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] ResolveRelatedTestProjects(
        IEnumerable<CSharpChangeImpactService.ImpactTestItem> relatedTests,
        IReadOnlyCollection<CSharpProjectOverviewAnalysisService.ProjectOverviewItem> projects,
        IReadOnlyCollection<ProjectDirectoryItem> projectDirectoryIndex)
    {
        var projectByName = projects.ToDictionary(project => project.Name, StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var test in relatedTests)
        {
            var projectName = ResolveProjectNameForPath(test.RelativePath, projectDirectoryIndex);
            if (string.IsNullOrWhiteSpace(projectName))
                continue;

            if (projectByName.TryGetValue(projectName, out var project) && project.IsTestProject)
                names.Add(project.Name);
        }

        return names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static PlanStepItem[] BuildInspectionSteps(
        IReadOnlyCollection<CSharpChangeImpactService.ImpactTargetItem> primaryTargets,
        CSharpChangeImpactService.ChangeImpactResponse impact,
        IReadOnlyCollection<string> runtimeSurfaceFiles,
        IReadOnlyCollection<string> relatedTestProjects,
        int maxResults)
    {
        var steps = new List<PlanStepItem>();
        if (primaryTargets.Count > 0)
        {
            steps.Add(new PlanStepItem(
                Order: steps.Count + 1,
                Phase: "inspect",
                Title: "Inspect primary change targets",
                Rationale: "Start from the declarations that best match the requested symbol or file.",
                Files: primaryTargets.Select(item => item.RelativePath).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).Take(maxResults).ToArray(),
                Commands: Array.Empty<string>()));
        }

        var callerFiles = impact.IncomingCalls
            .Select(item => item.RelativePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToArray();
        if (callerFiles.Length > 0)
        {
            steps.Add(new PlanStepItem(
                Order: steps.Count + 1,
                Phase: "inspect",
                Title: "Inspect direct callers",
                Rationale: "Review the immediate call sites before editing shared behavior.",
                Files: callerFiles,
                Commands: Array.Empty<string>()));
        }

        var relationFiles = impact.RelatedSymbols
            .Select(item => item.RelativePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToArray();
        if (relationFiles.Length > 0)
        {
            steps.Add(new PlanStepItem(
                Order: steps.Count + 1,
                Phase: "inspect",
                Title: "Inspect inheritance and override surfaces",
                Rationale: "Validate derived types, implementations, and overrides affected by the change.",
                Files: relationFiles,
                Commands: Array.Empty<string>()));
        }

        var runtimeFiles = runtimeSurfaceFiles
            .Take(maxResults)
            .ToArray();
        if (runtimeFiles.Length > 0)
        {
            steps.Add(new PlanStepItem(
                Order: steps.Count + 1,
                Phase: "inspect",
                Title: "Inspect runtime wiring",
                Rationale: "Confirm DI registrations, routes, handlers, or hosted services stay aligned with the change.",
                Files: runtimeFiles,
                Commands: Array.Empty<string>()));
        }

        if (relatedTestProjects.Count > 0)
        {
            steps.Add(new PlanStepItem(
                Order: steps.Count + 1,
                Phase: "inspect",
                Title: "Inspect related tests",
                Rationale: "Review the test projects most likely to encode current behavior before editing.",
                Files: relatedTestProjects.Take(maxResults).ToArray(),
                Commands: Array.Empty<string>()));
        }

        return steps.Take(maxResults).ToArray();
    }

    private static PlanStepItem[] BuildEditSteps(
        IReadOnlyCollection<CSharpChangeImpactService.ImpactTargetItem> primaryTargets,
        CSharpChangeImpactService.ChangeImpactResponse impact,
        IReadOnlyCollection<string> runtimeSurfaceFiles,
        int maxResults)
    {
        var steps = new List<PlanStepItem>();
        var targetFiles = primaryTargets
            .Select(item => item.RelativePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToArray();
        if (targetFiles.Length > 0)
        {
            steps.Add(new PlanStepItem(
                Order: steps.Count + 1,
                Phase: "edit",
                Title: "Edit primary implementation files",
                Rationale: "Apply the requested behavior change at the source before adjusting dependent code.",
                Files: targetFiles,
                Commands: Array.Empty<string>()));
        }

        var secondaryFiles = impact.IncomingCalls
            .Select(item => item.RelativePath)
            .Concat(impact.RelatedSymbols.Select(item => item.RelativePath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Except(targetFiles, StringComparer.OrdinalIgnoreCase)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToArray();
        if (secondaryFiles.Length > 0)
        {
            steps.Add(new PlanStepItem(
                Order: steps.Count + 1,
                Phase: "edit",
                Title: "Adjust dependent code paths",
                Rationale: "Update direct callers and inheritance surfaces that encode the old behavior or contract.",
                Files: secondaryFiles,
                Commands: Array.Empty<string>()));
        }

        var runtimeFiles = runtimeSurfaceFiles
            .Except(targetFiles, StringComparer.OrdinalIgnoreCase)
            .Except(secondaryFiles, StringComparer.OrdinalIgnoreCase)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToArray();
        if (runtimeFiles.Length > 0)
        {
            steps.Add(new PlanStepItem(
                Order: steps.Count + 1,
                Phase: "edit",
                Title: "Reconcile runtime wiring if needed",
                Rationale: "Only update DI or entrypoint wiring when the change alters runtime composition or contracts.",
                Files: runtimeFiles,
                Commands: Array.Empty<string>()));
        }

        return steps.Take(maxResults).ToArray();
    }

    private static VerificationStepItem[] BuildVerificationSteps(
        string workspacePath,
        CSharpProjectOverviewAnalysisService.ProjectOverviewResponse overview,
        IReadOnlyCollection<CSharpProjectOverviewAnalysisService.ProjectOverviewItem> impactedProjects,
        IReadOnlyCollection<string> relatedTestProjects,
        CSharpChangeImpactService.ChangeImpactResponse impact,
        int maxResults)
    {
        var steps = new List<VerificationStepItem>();
        var solutionFile = overview.SolutionFiles.FirstOrDefault();
        var buildTarget = !string.IsNullOrWhiteSpace(solutionFile)
            ? solutionFile
            : impactedProjects.FirstOrDefault(project => !project.IsTestProject)?.Path ?? overview.Projects.FirstOrDefault()?.Path;

        if (!string.IsNullOrWhiteSpace(buildTarget))
        {
            steps.Add(new VerificationStepItem(
                Order: steps.Count + 1,
                Kind: "build",
                Title: "Build the impacted workspace",
                Reason: "Compile the changed code before running narrower checks.",
                Command: $"dotnet build {buildTarget}",
                Targets: impactedProjects.Where(project => !project.IsTestProject).Select(project => project.Name).Take(maxResults).ToArray()));
        }

        var testProjects = overview.Projects
            .Where(project => project.IsTestProject && relatedTestProjects.Contains(project.Name, StringComparer.OrdinalIgnoreCase))
            .Select(project => project.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var testProject in testProjects.Take(maxResults))
        {
            steps.Add(new VerificationStepItem(
                Order: steps.Count + 1,
                Kind: "test",
                Title: $"Run {Path.GetFileNameWithoutExtension(testProject)}",
                Reason: "Exercise the most directly related tests first.",
                Command: $"dotnet test {testProject}",
                Targets: [testProject]));
        }

        if (testProjects.Length == 0 && !string.IsNullOrWhiteSpace(solutionFile) && overview.TestProjects.Length > 0)
        {
            steps.Add(new VerificationStepItem(
                Order: steps.Count + 1,
                Kind: "test",
                Title: "Run solution test pass",
                Reason: "No direct test project mapping was found, so run the workspace test entrypoint.",
                Command: $"dotnet test {solutionFile}",
                Targets: overview.TestProjects.Take(maxResults).ToArray()));
        }

        var diagnosticFocus = impact.RecommendedFiles
            .Select(item => item.RelativePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Take(maxResults)
            .ToArray();
        if (diagnosticFocus.Length > 0)
        {
            steps.Add(new VerificationStepItem(
                Order: steps.Count + 1,
                Kind: "diagnostics",
                Title: "Inspect diagnostics on the highest-signal files",
                Reason: "These files carried the strongest impact evidence and should be checked for regressions first.",
                Command: string.Empty,
                Targets: diagnosticFocus));
        }

        return steps.Take(maxResults).ToArray();
    }

    private static bool LooksLikeTestProject(string projectName)
        => projectName.Contains("Test", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeTestPath(string relativePath)
        => relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment =>
                string.Equals(segment, "test", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "tests", StringComparison.OrdinalIgnoreCase));

    private static string ResolveProjectNameForPath(
        string relativePath,
        IReadOnlyCollection<ProjectDirectoryItem> projectDirectoryIndex)
    {
        foreach (var project in projectDirectoryIndex)
        {
            if (relativePath.StartsWith(project.Directory, StringComparison.OrdinalIgnoreCase))
                return project.Name;
        }

        return string.Empty;
    }

    private static string GetProjectDirectory(string projectPath)
        => Path.GetDirectoryName(projectPath)?.Replace('\\', '/') ?? string.Empty;

    private sealed record ProjectDirectoryItem(
        string Name,
        string Directory);

    public sealed record ChangePlanResponse(
        string Summary,
        string WorkspaceRoot,
        string? Request,
        string? Query,
        string FilePath,
        bool GraphBuiltDuringRequest,
        DateTimeOffset GraphBuiltAtUtc,
        CSharpChangeImpactService.ImpactTargetItem[] PrimaryTargets,
        PlannedProjectItem[] ImpactedProjects,
        PlanStepItem[] InspectionSteps,
        PlanStepItem[] EditSteps,
        VerificationStepItem[] VerificationSteps,
        string[] SuggestedCommands,
        CSharpChangeImpactService.RecommendedFileItem[] RecommendedFiles,
        string[] Warnings,
        int TruncatedPrimaryTargets,
        int TruncatedImpactedProjects,
        int TruncatedInspectionSteps,
        int TruncatedEditSteps,
        int TruncatedVerificationSteps) : IStructuredToolResult;

    public sealed record PlannedProjectItem(
        string Name,
        string Path,
        string ProjectType,
        bool IsTestProject,
        string[] Entrypoints);

    public sealed record PlanStepItem(
        int Order,
        string Phase,
        string Title,
        string Rationale,
        string[] Files,
        string[] Commands);

    public sealed record VerificationStepItem(
        int Order,
        string Kind,
        string Title,
        string Reason,
        string Command,
        string[] Targets);
}
