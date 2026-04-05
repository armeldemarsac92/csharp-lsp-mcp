using System.Xml.Linq;
using CSharpLspMcp.Contracts.Common;
using CSharpLspMcp.Workspace;

namespace CSharpLspMcp.Analysis.Architecture;

public sealed class CSharpProjectOverviewAnalysisService
{
    private static readonly string[] IgnoredPathSegments = [".git", ".idea", ".vs", "bin", "obj", "node_modules"];
    private static readonly string[] SolutionFileExtensions = [".sln", ".slnx"];
    private readonly WorkspaceState _workspaceState;

    public CSharpProjectOverviewAnalysisService(WorkspaceState workspaceState)
    {
        _workspaceState = workspaceState;
    }

    public Task<ProjectOverviewResponse> GetProjectOverviewAsync(
        int maxProjects,
        int maxPackagesPerProject,
        int maxProjectReferencesPerProject,
        CancellationToken cancellationToken)
    {
        var workspacePath = _workspaceState.CurrentPath;
        if (string.IsNullOrWhiteSpace(workspacePath))
            throw new InvalidOperationException("Workspace is not set. Call csharp_set_workspace first.");

        cancellationToken.ThrowIfCancellationRequested();

        var solutionFiles = Directory
            .EnumerateFiles(workspacePath, "*", SearchOption.TopDirectoryOnly)
            .Where(IsSolutionFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var projects = Directory
            .EnumerateFiles(workspacePath, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !ContainsIgnoredPathSegment(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => ParseProject(path, workspacePath))
            .ToArray();

        if (projects.Length == 0)
            return Task.FromResult(
                new ProjectOverviewResponse(
                    Summary: $"No C# project files were found under {workspacePath}.",
                    SolutionRoot: workspacePath,
                    SolutionFiles: solutionFiles.Select(path => Path.GetRelativePath(workspacePath, path)).ToArray(),
                    TotalProjects: 0,
                    Projects: Array.Empty<ProjectOverviewItem>(),
                    TestProjects: Array.Empty<string>(),
                    EntrypointProjects: Array.Empty<string>(),
                    ProjectGraph: Array.Empty<ProjectGraphEdge>(),
                    SuggestedCommands: Array.Empty<string>(),
                    TruncatedProjects: 0));

        var testProjects = projects.Where(project => project.IsTestProject).ToArray();
        var entryPointProjects = projects.Where(project => project.EntrypointFiles.Length > 0).ToArray();

        var graphLines = projects
            .Where(project => project.ProjectReferences.Length > 0)
            .Select(project => new ProjectGraphEdge(
                project.Name,
                project.ProjectReferences.Take(maxProjectReferencesPerProject).ToArray(),
                Math.Max(0, project.ProjectReferences.Length - maxProjectReferencesPerProject)))
            .Take(maxProjects)
            .ToArray();

        var suggestedCommands = new List<string>();
        if (solutionFiles.Length > 0)
        {
            var primarySolution = Path.GetRelativePath(workspacePath, solutionFiles[0]);
            suggestedCommands.Add($"dotnet build {primarySolution}");
            if (testProjects.Length > 0)
                suggestedCommands.Add($"dotnet test {primarySolution}");
        }
        else
        {
            suggestedCommands.Add($"dotnet build {projects[0].RelativePath}");
            if (testProjects.Length > 0)
                suggestedCommands.Add("dotnet test <test-project.csproj>");
        }

        var visibleProjects = projects
            .Take(maxProjects)
            .Select(project => new ProjectOverviewItem(
                project.Name,
                project.RelativePath,
                project.ProjectType,
                project.TargetFrameworks,
                project.OutputType,
                project.Nullable,
                project.ImplicitUsings,
                project.IsTestProject,
                project.EntrypointFiles,
                project.ProjectReferences.Take(maxProjectReferencesPerProject).ToArray(),
                Math.Max(0, project.ProjectReferences.Length - maxProjectReferencesPerProject),
                project.PackageReferences.Take(maxPackagesPerProject).ToArray(),
                Math.Max(0, project.PackageReferences.Length - maxPackagesPerProject)))
            .ToArray();

        return Task.FromResult(
            new ProjectOverviewResponse(
                Summary: $"Found {projects.Length} project(s), {testProjects.Length} test project(s), and {entryPointProjects.Length} entrypoint project(s).",
                SolutionRoot: workspacePath,
                SolutionFiles: solutionFiles.Select(path => Path.GetRelativePath(workspacePath, path)).ToArray(),
                TotalProjects: projects.Length,
                Projects: visibleProjects,
                TestProjects: testProjects.Select(project => project.Name).ToArray(),
                EntrypointProjects: entryPointProjects.Select(project => project.Name).ToArray(),
                ProjectGraph: graphLines,
                SuggestedCommands: suggestedCommands.ToArray(),
                TruncatedProjects: Math.Max(0, projects.Length - maxProjects)));
    }

    private static ProjectOverview ParseProject(string projectPath, string workspacePath)
    {
        var document = XDocument.Load(projectPath);
        var root = document.Root;
        var projectName = Path.GetFileNameWithoutExtension(projectPath);

        var packageReferences = document
            .Descendants()
            .Where(element => element.Name.LocalName == "PackageReference")
            .Select(element => BuildPackageReference(element))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var projectReferences = document
            .Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => NormalizeProjectReference(value!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var targetFrameworks = ReadPropertyValues(document, "TargetFrameworks")
            .SelectMany(value => value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Concat(ReadPropertyValues(document, "TargetFramework"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var outputType = ReadPropertyValues(document, "OutputType").LastOrDefault() ?? "Library";
        var nullable = ReadPropertyValues(document, "Nullable").LastOrDefault() ?? "default";
        var implicitUsings = ReadPropertyValues(document, "ImplicitUsings").LastOrDefault() ?? "default";
        var isTestProject = ReadPropertyValues(document, "IsTestProject")
            .Any(value => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)) ||
            projectName.Contains("Test", StringComparison.OrdinalIgnoreCase) ||
            LooksLikeTestProjectPath(projectPath) ||
            packageReferences.Any(package => package.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase)) ||
            packageReferences.Any(package =>
                package.Contains("xunit", StringComparison.OrdinalIgnoreCase) ||
                package.Contains("nunit", StringComparison.OrdinalIgnoreCase) ||
                package.Contains("MSTest", StringComparison.OrdinalIgnoreCase));

        var entrypointFiles = FindEntrypointFiles(Path.GetDirectoryName(projectPath)!, workspacePath);

        return new ProjectOverview(
            projectName,
            Path.GetRelativePath(workspacePath, projectPath),
            InferProjectType(root?.Attribute("Sdk")?.Value, outputType, projectName, isTestProject, packageReferences),
            targetFrameworks,
            outputType,
            nullable,
            implicitUsings,
            isTestProject,
            packageReferences,
            projectReferences,
            entrypointFiles);
    }

    private static string BuildPackageReference(XElement element)
    {
        var include = element.Attribute("Include")?.Value ?? string.Empty;
        var version = element.Attribute("Version")?.Value ??
                      element.Elements().FirstOrDefault(child => child.Name.LocalName == "Version")?.Value;

        return string.IsNullOrWhiteSpace(version)
            ? include
            : $"{include} ({version})";
    }

    private static string[] ReadPropertyValues(XDocument document, string propertyName)
    {
        return document
            .Descendants()
            .Where(element => element.Name.LocalName == propertyName)
            .Select(element => element.Value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
    }

    private static string[] FindEntrypointFiles(string projectDirectory, string workspacePath)
    {
        var entrypoints = new List<string>();
        var programPath = Path.Combine(projectDirectory, "Program.cs");
        if (File.Exists(programPath))
            entrypoints.Add(Path.GetRelativePath(workspacePath, programPath));

        var workerFiles = Directory
            .EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !ContainsIgnoredPathSegment(path))
            .Where(path =>
            {
                var fileName = Path.GetFileName(path);
                return fileName.EndsWith("Worker.cs", StringComparison.OrdinalIgnoreCase) ||
                       fileName.EndsWith("HostedService.cs", StringComparison.OrdinalIgnoreCase) ||
                       fileName.EndsWith("BackgroundService.cs", StringComparison.OrdinalIgnoreCase);
            })
            .Select(path => Path.GetRelativePath(workspacePath, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(3);

        entrypoints.AddRange(workerFiles);
        entrypoints.AddRange(FindLambdaHandlerFiles(projectDirectory, workspacePath));

        return entrypoints
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> FindLambdaHandlerFiles(string projectDirectory, string workspacePath)
    {
        return Directory
            .EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !ContainsIgnoredPathSegment(path))
            .Where(IsLambdaHandlerFile)
            .Select(path => Path.GetRelativePath(workspacePath, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(3);
    }

    private static bool IsLambdaHandlerFile(string filePath)
    {
        var content = File.ReadAllText(filePath);
        return content.Contains("ILambdaContext", StringComparison.Ordinal) &&
               content.Contains("Handler(", StringComparison.Ordinal);
    }

    private static bool ContainsIgnoredPathSegment(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment => IgnoredPathSegments.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }

    private static string NormalizeProjectReference(string include)
    {
        var normalizedPath = include
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        return Path.GetFileNameWithoutExtension(normalizedPath);
    }

    private static string InferProjectType(
        string? sdk,
        string outputType,
        string projectName,
        bool isTestProject,
        IReadOnlyCollection<string> packageReferences)
    {
        if (isTestProject)
            return "test";

        if (!string.IsNullOrWhiteSpace(sdk) &&
            sdk.Contains("Web", StringComparison.OrdinalIgnoreCase))
        {
            return "web";
        }

        if (projectName.Contains("Lambda", StringComparison.OrdinalIgnoreCase) ||
            packageReferences.Any(package => package.Contains("Amazon.Lambda", StringComparison.OrdinalIgnoreCase)))
        {
            return "lambda";
        }

        if (string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase))
        {
            if (projectName.Contains("Worker", StringComparison.OrdinalIgnoreCase) ||
                packageReferences.Any(package => package.Contains("Hosting", StringComparison.OrdinalIgnoreCase)))
            {
                return "worker";
            }

            return "executable";
        }

        return "classlib";
    }

    private static bool IsSolutionFile(string path)
        => SolutionFileExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static bool LooksLikeTestProjectPath(string path)
        => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment =>
                string.Equals(segment, "test", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "tests", StringComparison.OrdinalIgnoreCase));

    private sealed record ProjectOverview(
        string Name,
        string RelativePath,
        string ProjectType,
        string[] TargetFrameworks,
        string OutputType,
        string Nullable,
        string ImplicitUsings,
        bool IsTestProject,
        string[] PackageReferences,
        string[] ProjectReferences,
        string[] EntrypointFiles);

    public sealed record ProjectOverviewResponse(
        string Summary,
        string SolutionRoot,
        string[] SolutionFiles,
        int TotalProjects,
        ProjectOverviewItem[] Projects,
        string[] TestProjects,
        string[] EntrypointProjects,
        ProjectGraphEdge[] ProjectGraph,
        string[] SuggestedCommands,
        int TruncatedProjects) : IStructuredToolResult;

    public sealed record ProjectOverviewItem(
        string Name,
        string Path,
        string ProjectType,
        string[] TargetFrameworks,
        string OutputType,
        string Nullable,
        string ImplicitUsings,
        bool IsTestProject,
        string[] Entrypoints,
        string[] ProjectReferences,
        int TruncatedProjectReferences,
        string[] PackageReferences,
        int TruncatedPackageReferences);

    public sealed record ProjectGraphEdge(
        string Project,
        string[] References,
        int TruncatedReferences);
}
