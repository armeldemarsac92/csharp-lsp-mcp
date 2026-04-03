using System.Text;
using System.Xml.Linq;
using CSharpLspMcp.Workspace;

namespace CSharpLspMcp.Analysis.Architecture;

public sealed class CSharpProjectOverviewAnalysisService
{
    private static readonly string[] IgnoredPathSegments = [".git", ".idea", ".vs", "bin", "obj", "node_modules"];
    private readonly WorkspaceState _workspaceState;

    public CSharpProjectOverviewAnalysisService(WorkspaceState workspaceState)
    {
        _workspaceState = workspaceState;
    }

    public Task<string> GetProjectOverviewAsync(
        int maxProjects,
        int maxPackagesPerProject,
        int maxProjectReferencesPerProject,
        CancellationToken cancellationToken)
    {
        var workspacePath = _workspaceState.CurrentPath;
        if (string.IsNullOrWhiteSpace(workspacePath))
            return Task.FromResult("Error: Workspace is not set. Call csharp_set_workspace first.");

        cancellationToken.ThrowIfCancellationRequested();

        var solutionFiles = Directory
            .EnumerateFiles(workspacePath, "*.sln*", SearchOption.TopDirectoryOnly)
            .Where(path => !Path.GetFileName(path).EndsWith(".slnf", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var projects = Directory
            .EnumerateFiles(workspacePath, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !ContainsIgnoredPathSegment(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => ParseProject(path, workspacePath))
            .ToArray();

        if (projects.Length == 0)
            return Task.FromResult($"No C# project files were found under {workspacePath}.");

        var testProjects = projects.Where(project => project.IsTestProject).ToArray();
        var entryPointProjects = projects.Where(project => project.EntrypointFiles.Length > 0).ToArray();

        var sb = new StringBuilder();
        sb.AppendLine($"Solution root: {workspacePath}");

        if (solutionFiles.Length > 0)
        {
            sb.AppendLine($"Solutions ({solutionFiles.Length}):");
            foreach (var solutionFile in solutionFiles)
                sb.AppendLine($"• {Path.GetRelativePath(workspacePath, solutionFile)}");
            sb.AppendLine();
        }

        sb.AppendLine($"Projects ({projects.Length}):");
        foreach (var project in projects.Take(maxProjects))
        {
            sb.AppendLine($"• {project.Name} [{project.ProjectType}]");
            sb.AppendLine($"  Path: {project.RelativePath}");
            sb.AppendLine($"  Frameworks: {FormatList(project.TargetFrameworks, "unknown")}");
            sb.AppendLine($"  Nullable: {project.Nullable}, ImplicitUsings: {project.ImplicitUsings}");

            if (!string.IsNullOrWhiteSpace(project.OutputType))
                sb.AppendLine($"  OutputType: {project.OutputType}");

            if (project.EntrypointFiles.Length > 0)
                sb.AppendLine($"  Entrypoints: {FormatList(project.EntrypointFiles)}");

            if (project.ProjectReferences.Length > 0)
            {
                sb.AppendLine(
                    $"  Project refs: {FormatList(project.ProjectReferences.Take(maxProjectReferencesPerProject))}");
                if (project.ProjectReferences.Length > maxProjectReferencesPerProject)
                    sb.AppendLine($"  ... and {project.ProjectReferences.Length - maxProjectReferencesPerProject} more project reference(s)");
            }

            if (project.PackageReferences.Length > 0)
            {
                sb.AppendLine(
                    $"  Packages: {FormatList(project.PackageReferences.Take(maxPackagesPerProject))}");
                if (project.PackageReferences.Length > maxPackagesPerProject)
                    sb.AppendLine($"  ... and {project.PackageReferences.Length - maxPackagesPerProject} more package reference(s)");
            }
        }

        if (projects.Length > maxProjects)
            sb.AppendLine($"\n... and {projects.Length - maxProjects} more project(s)");

        sb.AppendLine();
        sb.AppendLine($"Test projects ({testProjects.Length}): {FormatList(testProjects.Select(project => project.Name), "none")}");
        sb.AppendLine($"Entrypoint projects ({entryPointProjects.Length}): {FormatList(entryPointProjects.Select(project => project.Name), "none")}");

        var graphLines = projects
            .Where(project => project.ProjectReferences.Length > 0)
            .Select(project => $"• {project.Name} -> {FormatList(project.ProjectReferences.Take(maxProjectReferencesPerProject))}")
            .Take(maxProjects)
            .ToArray();

        if (graphLines.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Project graph:");
            foreach (var graphLine in graphLines)
                sb.AppendLine(graphLine);
        }

        sb.AppendLine();
        sb.AppendLine("Suggested commands:");
        if (solutionFiles.Length > 0)
        {
            var primarySolution = Path.GetRelativePath(workspacePath, solutionFiles[0]);
            sb.AppendLine($"• dotnet build {primarySolution}");
            if (testProjects.Length > 0)
                sb.AppendLine($"• dotnet test {primarySolution}");
        }
        else
        {
            sb.AppendLine($"• dotnet build {projects[0].RelativePath}");
            if (testProjects.Length > 0)
                sb.AppendLine("• dotnet test <test-project.csproj>");
        }

        return Task.FromResult(sb.ToString());
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

        return entrypoints
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    private static string FormatList(IEnumerable<string> values, string fallback = "none")
    {
        var materializedValues = values.ToArray();
        return materializedValues.Length == 0
            ? fallback
            : string.Join(", ", materializedValues);
    }

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
}
