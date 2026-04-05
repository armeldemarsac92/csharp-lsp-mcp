using CSharpLspMcp.Analysis.Architecture;
using CSharpLspMcp.Analysis.Lsp;
using CSharpLspMcp.Analysis.Planning;
using CSharpLspMcp.Contracts.Common;
using CSharpLspMcp.Workspace;

namespace CSharpLspMcp.Analysis.Verification;

public sealed class CSharpChangeVerificationService
{
    private readonly IWorkspaceDiagnosticsProvider _diagnosticsProvider;
    private readonly CSharpChangePlanService _changePlanService;
    private readonly CSharpProjectOverviewAnalysisService _projectOverviewAnalysisService;
    private readonly WorkspaceState _workspaceState;

    public CSharpChangeVerificationService(
        IWorkspaceDiagnosticsProvider diagnosticsProvider,
        CSharpChangePlanService changePlanService,
        CSharpProjectOverviewAnalysisService projectOverviewAnalysisService,
        WorkspaceState workspaceState)
    {
        _diagnosticsProvider = diagnosticsProvider;
        _changePlanService = changePlanService;
        _projectOverviewAnalysisService = projectOverviewAnalysisService;
        _workspaceState = workspaceState;
    }

    public async Task<ChangeVerificationResponse> VerifyAsync(
        string? request,
        string? symbolQuery,
        string? filePath,
        string[]? changedFiles,
        bool includeTests,
        bool rebuildIfMissing,
        string minimumSeverity,
        string[]? excludeDiagnosticCodes,
        string[]? excludeDiagnosticSources,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var workspacePath = _workspaceState.CurrentPath;
        if (string.IsNullOrWhiteSpace(workspacePath))
            throw new InvalidOperationException("Workspace is not set. Call csharp_set_workspace first.");

        var effectiveMaxResults = Math.Max(1, maxResults);
        var normalizedChangedFiles = NormalizeChangedFiles(changedFiles, workspacePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (string.IsNullOrWhiteSpace(symbolQuery) &&
            string.IsNullOrWhiteSpace(filePath) &&
            normalizedChangedFiles.Length == 0)
        {
            throw new InvalidOperationException("Provide symbolQuery, filePath, or changedFiles.");
        }

        var overview = await _projectOverviewAnalysisService.GetProjectOverviewAsync(
            maxProjects: 250,
            maxPackagesPerProject: 6,
            maxProjectReferencesPerProject: 20,
            cancellationToken);
        var projectDirectoryIndex = overview.Projects
            .Where(project => !string.IsNullOrWhiteSpace(project.Path))
            .Select(project => new ProjectDirectoryItem(project.Name, GetProjectDirectory(project.Path)))
            .OrderByDescending(project => project.Directory.Length)
            .ToArray();

        var plan = !string.IsNullOrWhiteSpace(symbolQuery) || !string.IsNullOrWhiteSpace(filePath)
            ? await _changePlanService.PlanAsync(
                request,
                symbolQuery,
                filePath,
                includeTests,
                rebuildIfMissing,
                Math.Max(10, effectiveMaxResults * 2),
                cancellationToken)
            : null;

        var impactedProjects = BuildImpactedProjects(overview, projectDirectoryIndex, normalizedChangedFiles, plan)
            .Take(effectiveMaxResults)
            .ToArray();
        var buildCommands = BuildBuildCommands(overview, impactedProjects, plan, effectiveMaxResults);
        var testCommands = BuildTestCommands(overview, impactedProjects, normalizedChangedFiles, plan, effectiveMaxResults);
        var diagnosticFocusFiles = BuildDiagnosticFocusFiles(normalizedChangedFiles, plan, effectiveMaxResults);

        var diagnostics = await _diagnosticsProvider.GetWorkspaceDiagnosticsAsync(
            maxDocuments: Math.Max(100, effectiveMaxResults * 10),
            maxDiagnosticsPerDocument: 10,
            minimumSeverity: minimumSeverity,
            includeGenerated: false,
            includeTests: includeTests,
            excludePaths: null,
            excludeDiagnosticCodes: excludeDiagnosticCodes,
            excludeDiagnosticSources: excludeDiagnosticSources,
            cancellationToken);
        var focusedDiagnostics = FilterFocusedDiagnostics(diagnostics, diagnosticFocusFiles, workspacePath)
            .Take(effectiveMaxResults)
            .ToArray();
        var totalFocusedDiagnostics = FilterFocusedDiagnostics(diagnostics, diagnosticFocusFiles, workspacePath).Length;

        var verificationSteps = BuildVerificationSteps(buildCommands, testCommands, diagnosticFocusFiles, focusedDiagnostics, effectiveMaxResults);
        var warnings = new List<string>();
        if (plan != null)
            warnings.AddRange(plan.Warnings);
        if (diagnosticFocusFiles.Length == 0 && normalizedChangedFiles.Length == 0)
            warnings.Add("No changed files were supplied, so diagnostic focus was inferred from the change plan.");

        return new ChangeVerificationResponse(
            Summary: BuildSummary(request, buildCommands.Length, testCommands.Length, focusedDiagnostics.Length),
            WorkspaceRoot: workspacePath,
            Request: request,
            Query: symbolQuery,
            FilePath: string.IsNullOrWhiteSpace(filePath) ? string.Empty : filePath,
            ChangedFiles: normalizedChangedFiles,
            GraphBuiltDuringRequest: plan?.GraphBuiltDuringRequest ?? false,
            GraphBuiltAtUtc: plan?.GraphBuiltAtUtc,
            ImpactedProjects: impactedProjects,
            BuildCommands: buildCommands,
            TestCommands: testCommands,
            DiagnosticFocusFiles: diagnosticFocusFiles,
            Diagnostics: focusedDiagnostics,
            VerificationSteps: verificationSteps,
            Warnings: warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            TruncatedImpactedProjects: Math.Max(0, BuildImpactedProjects(overview, projectDirectoryIndex, normalizedChangedFiles, plan).Length - impactedProjects.Length),
            TruncatedBuildCommands: Math.Max(0, BuildBuildCommands(overview, impactedProjects, plan, int.MaxValue).Length - buildCommands.Length),
            TruncatedTestCommands: Math.Max(0, BuildTestCommands(overview, impactedProjects, normalizedChangedFiles, plan, int.MaxValue).Length - testCommands.Length),
            TruncatedDiagnostics: Math.Max(0, totalFocusedDiagnostics - focusedDiagnostics.Length),
            TruncatedVerificationSteps: Math.Max(0, BuildVerificationSteps(buildCommands, testCommands, diagnosticFocusFiles, focusedDiagnostics, int.MaxValue).Length - verificationSteps.Length));
    }

    private static string BuildSummary(string? request, int buildCommandCount, int testCommandCount, int diagnosticDocumentCount)
    {
        var prefix = string.IsNullOrWhiteSpace(request)
            ? "Prepared verification plan"
            : $"Prepared verification plan for '{request}'";
        return $"{prefix} with {buildCommandCount} build command(s), {testCommandCount} test command(s), and {diagnosticDocumentCount} focused diagnostic document(s).";
    }

    private static string[] NormalizeChangedFiles(string[]? changedFiles, string workspacePath)
    {
        return changedFiles?
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path =>
            {
                var absolutePath = Path.IsPathRooted(path)
                    ? Path.GetFullPath(path)
                    : Path.GetFullPath(Path.Combine(workspacePath, path));
                return absolutePath.StartsWith(workspacePath, StringComparison.OrdinalIgnoreCase)
                    ? Path.GetRelativePath(workspacePath, absolutePath)
                    : absolutePath;
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();
    }

    private static CSharpChangePlanService.PlannedProjectItem[] BuildImpactedProjects(
        CSharpProjectOverviewAnalysisService.ProjectOverviewResponse overview,
        IReadOnlyCollection<ProjectDirectoryItem> projectDirectoryIndex,
        IReadOnlyCollection<string> changedFiles,
        CSharpChangePlanService.ChangePlanResponse? plan)
    {
        var impacted = new Dictionary<string, CSharpChangePlanService.PlannedProjectItem>(StringComparer.OrdinalIgnoreCase);
        if (plan != null)
        {
            foreach (var project in plan.ImpactedProjects)
                impacted.TryAdd(project.Name, project);
        }

        foreach (var changedFile in changedFiles)
        {
            var projectName = ResolveProjectNameForPath(changedFile, projectDirectoryIndex);
            if (string.IsNullOrWhiteSpace(projectName))
                continue;

            var project = overview.Projects.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (project == null)
                continue;

            impacted.TryAdd(project.Name, new CSharpChangePlanService.PlannedProjectItem(
                project.Name,
                project.Path,
                project.ProjectType,
                project.IsTestProject,
                project.Entrypoints));
        }

        return impacted.Values
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] BuildBuildCommands(
        CSharpProjectOverviewAnalysisService.ProjectOverviewResponse overview,
        IReadOnlyCollection<CSharpChangePlanService.PlannedProjectItem> impactedProjects,
        CSharpChangePlanService.ChangePlanResponse? plan,
        int maxResults)
    {
        if (plan != null)
        {
            var planned = plan.VerificationSteps
                .Where(step => string.Equals(step.Kind, "build", StringComparison.OrdinalIgnoreCase))
                .Select(step => step.Command)
                .Where(command => !string.IsNullOrWhiteSpace(command))
                .Distinct(StringComparer.Ordinal)
                .Take(maxResults)
                .ToArray();
            if (planned.Length > 0)
                return planned;
        }

        var solutionFile = overview.SolutionFiles.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(solutionFile))
            return [$"dotnet build {solutionFile}"];

        return impactedProjects
            .Where(project => !project.IsTestProject)
            .Select(project => $"dotnet build {project.Path}")
            .Distinct(StringComparer.Ordinal)
            .Take(maxResults)
            .ToArray();
    }

    private static string[] BuildTestCommands(
        CSharpProjectOverviewAnalysisService.ProjectOverviewResponse overview,
        IReadOnlyCollection<CSharpChangePlanService.PlannedProjectItem> impactedProjects,
        IReadOnlyCollection<string> changedFiles,
        CSharpChangePlanService.ChangePlanResponse? plan,
        int maxResults)
    {
        if (plan != null)
        {
            var planned = plan.VerificationSteps
                .Where(step => string.Equals(step.Kind, "test", StringComparison.OrdinalIgnoreCase))
                .Select(step => step.Command)
                .Where(command => !string.IsNullOrWhiteSpace(command))
                .Distinct(StringComparer.Ordinal)
                .Take(maxResults)
                .ToArray();
            if (planned.Length > 0)
                return planned;
        }

        var changedTestProjects = impactedProjects
            .Where(project => project.IsTestProject)
            .Select(project => $"dotnet test {project.Path}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (changedTestProjects.Length > 0)
            return changedTestProjects.Take(maxResults).ToArray();

        if (changedFiles.Any(file => LooksLikeTestPath(file)))
        {
            return impactedProjects
                .Where(project => project.IsTestProject)
                .Select(project => $"dotnet test {project.Path}")
                .Distinct(StringComparer.Ordinal)
                .Take(maxResults)
                .ToArray();
        }

        var solutionFile = overview.SolutionFiles.FirstOrDefault();
        return !string.IsNullOrWhiteSpace(solutionFile) && overview.TestProjects.Length > 0
            ? [$"dotnet test {solutionFile}"]
            : Array.Empty<string>();
    }

    private static string[] BuildDiagnosticFocusFiles(
        IReadOnlyCollection<string> changedFiles,
        CSharpChangePlanService.ChangePlanResponse? plan,
        int maxResults)
    {
        var files = new List<string>();
        files.AddRange(changedFiles);
        if (plan != null)
        {
            files.AddRange(plan.PrimaryTargets.Select(target => target.RelativePath));
            files.AddRange(plan.RecommendedFiles.Select(file => file.RelativePath));
            files.AddRange(plan.VerificationSteps
                .Where(step => string.Equals(step.Kind, "diagnostics", StringComparison.OrdinalIgnoreCase))
                .SelectMany(step => step.Targets));
        }

        return files
            .Where(file => !string.IsNullOrWhiteSpace(file))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToArray();
    }

    private static FocusedDiagnosticDocument[] FilterFocusedDiagnostics(
        CSharpWorkspaceAnalysisService.WorkspaceDiagnosticsResponse diagnostics,
        IReadOnlyCollection<string> focusFiles,
        string workspacePath)
    {
        if (focusFiles.Count == 0)
            return diagnostics.Documents
                .Select(document => MapDocument(document, workspacePath))
                .ToArray();

        var normalizedFocusFiles = focusFiles
            .Select(path => NormalizeRelativePath(path, workspacePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return diagnostics.Documents
            .Where(document => normalizedFocusFiles.Contains(NormalizeRelativePath(document.FilePath, workspacePath)))
            .Select(document => MapDocument(document, workspacePath))
            .ToArray();
    }

    private static VerificationStepItem[] BuildVerificationSteps(
        IReadOnlyCollection<string> buildCommands,
        IReadOnlyCollection<string> testCommands,
        IReadOnlyCollection<string> diagnosticFocusFiles,
        IReadOnlyCollection<FocusedDiagnosticDocument> diagnostics,
        int maxResults)
    {
        var steps = new List<VerificationStepItem>();
        foreach (var command in buildCommands.Take(maxResults))
        {
            steps.Add(new VerificationStepItem(
                steps.Count + 1,
                "build",
                "Build changed projects",
                "Compile the affected code before evaluating diagnostics or tests.",
                command,
                Array.Empty<string>()));
        }

        foreach (var command in testCommands.Take(maxResults))
        {
            steps.Add(new VerificationStepItem(
                steps.Count + 1,
                "test",
                $"Run {command["dotnet test ".Length..]}",
                "Run the test projects most likely to catch regressions from the change.",
                command,
                Array.Empty<string>()));
        }

        if (diagnosticFocusFiles.Count > 0 || diagnostics.Count > 0)
        {
            steps.Add(new VerificationStepItem(
                steps.Count + 1,
                "diagnostics",
                "Inspect focused diagnostics",
                diagnostics.Count > 0
                    ? $"Review diagnostics on {diagnostics.Count} focused file(s)."
                    : "No current diagnostics were reported on the focused files.",
                string.Empty,
                diagnosticFocusFiles.Take(maxResults).ToArray()));
        }

        return steps.Take(maxResults).ToArray();
    }

    private static FocusedDiagnosticDocument MapDocument(
        CSharpWorkspaceAnalysisService.WorkspaceDiagnosticDocument document,
        string workspacePath)
    {
        return new FocusedDiagnosticDocument(
            NormalizeRelativePath(document.FilePath, workspacePath),
            document.DiagnosticCount,
            document.Diagnostics.Select(diagnostic => new FocusedDiagnosticItem(
                diagnostic.Severity,
                diagnostic.Code,
                diagnostic.Source,
                diagnostic.Line,
                diagnostic.Character,
                diagnostic.Message)).ToArray(),
            document.TruncatedDiagnostics);
    }

    private static bool LooksLikeTestPath(string path)
        => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment =>
                string.Equals(segment, "test", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "tests", StringComparison.OrdinalIgnoreCase));

    private static string ResolveProjectNameForPath(string relativePath, IReadOnlyCollection<ProjectDirectoryItem> projectDirectoryIndex)
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

    private static string NormalizeRelativePath(string path, string workspacePath)
    {
        var absolutePath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(workspacePath, path));
        return absolutePath.StartsWith(workspacePath, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(workspacePath, absolutePath)
            : absolutePath;
    }

    private sealed record ProjectDirectoryItem(
        string Name,
        string Directory);

    public sealed record ChangeVerificationResponse(
        string Summary,
        string WorkspaceRoot,
        string? Request,
        string? Query,
        string FilePath,
        string[] ChangedFiles,
        bool GraphBuiltDuringRequest,
        DateTimeOffset? GraphBuiltAtUtc,
        CSharpChangePlanService.PlannedProjectItem[] ImpactedProjects,
        string[] BuildCommands,
        string[] TestCommands,
        string[] DiagnosticFocusFiles,
        FocusedDiagnosticDocument[] Diagnostics,
        VerificationStepItem[] VerificationSteps,
        string[] Warnings,
        int TruncatedImpactedProjects,
        int TruncatedBuildCommands,
        int TruncatedTestCommands,
        int TruncatedDiagnostics,
        int TruncatedVerificationSteps) : IStructuredToolResult;

    public sealed record FocusedDiagnosticDocument(
        string RelativePath,
        int DiagnosticCount,
        FocusedDiagnosticItem[] Diagnostics,
        int TruncatedDiagnostics);

    public sealed record FocusedDiagnosticItem(
        string Severity,
        string? Code,
        string? Source,
        int Line,
        int Character,
        string Message);

    public sealed record VerificationStepItem(
        int Order,
        string Kind,
        string Title,
        string Reason,
        string Command,
        string[] Targets);
}
