using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CSharpLspMcp.Workspace;

namespace CSharpLspMcp.Analysis.Architecture;

public sealed class CSharpEntrypointAnalysisService
{
    private static readonly string[] IgnoredPathSegments = [".git", ".idea", ".vs", "bin", "obj", "node_modules"];
    private static readonly Regex MiddlewareRegex = new(@"\bapp\.(Use[A-Z][A-Za-z0-9_]*)\s*\(", RegexOptions.Compiled);
    private static readonly Regex RouteRegex = new(@"\b\w+\.(Map(?:Get|Post|Put|Delete|Patch|Group))\s*\(", RegexOptions.Compiled);
    private static readonly Regex HostedServiceRegistrationRegex = new(@"AddHostedService<(?<service>[^>]+)>", RegexOptions.Compiled);
    private static readonly Regex BackgroundServiceImplementationRegex = new(
        @"class\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*:\s*BackgroundService\b",
        RegexOptions.Compiled);
    private readonly WorkspaceState _workspaceState;

    public CSharpEntrypointAnalysisService(WorkspaceState workspaceState)
    {
        _workspaceState = workspaceState;
    }

    public Task<string> FindEntrypointsAsync(
        bool includeAspNetRoutes,
        bool includeHostedServices,
        bool includeMiddlewarePipeline,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var workspacePath = _workspaceState.CurrentPath;
        if (string.IsNullOrWhiteSpace(workspacePath))
            return Task.FromResult("Error: Workspace is not set. Call csharp_set_workspace first.");

        cancellationToken.ThrowIfCancellationRequested();

        var effectiveMaxResults = Math.Max(1, maxResults);
        var projects = EnumerateProjects(workspacePath);
        var hostProjects = projects
            .Where(project => project.ProgramPath != null || project.ProjectType is "worker" or "web" or "executable")
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var routeRegistrations = includeAspNetRoutes
            ? FindRouteRegistrations(workspacePath).ToArray()
            : Array.Empty<SourceLineMatch>();
        var hostedServiceRegistrations = includeHostedServices
            ? FindHostedServiceRegistrations(workspacePath).ToArray()
            : Array.Empty<SourceLineMatch>();
        var hostedServiceImplementations = includeHostedServices
            ? FindBackgroundServiceImplementations(workspacePath).ToArray()
            : Array.Empty<SourceLineMatch>();

        var sb = new StringBuilder();
        sb.AppendLine($"Solution root: {workspacePath}");
        sb.AppendLine();
        sb.AppendLine($"Host Projects ({hostProjects.Length}):");

        if (hostProjects.Length == 0)
        {
            sb.AppendLine("None.");
        }
        else
        {
            foreach (var project in hostProjects.Take(effectiveMaxResults))
            {
                sb.AppendLine($"• {project.Name} [{project.ProjectType}]");
                sb.AppendLine($"  Project: {project.RelativeProjectPath}");

                if (project.ProgramPath != null)
                    sb.AppendLine($"  Program: {project.ProgramPath}");

                if (includeMiddlewarePipeline && project.MiddlewareCalls.Length > 0)
                    sb.AppendLine($"  Middleware: {string.Join(", ", project.MiddlewareCalls)}");

                if (project.EndpointCompositionCalls.Length > 0)
                    sb.AppendLine($"  Endpoint composition: {string.Join(", ", project.EndpointCompositionCalls)}");
            }

            if (hostProjects.Length > effectiveMaxResults)
                sb.AppendLine($"... and {hostProjects.Length - effectiveMaxResults} more");
        }

        if (includeAspNetRoutes)
        {
            sb.AppendLine();
            sb.AppendLine($"ASP.NET Route Registrations ({routeRegistrations.Length}):");
            AppendSourceLineMatches(sb, routeRegistrations, effectiveMaxResults);
        }

        if (includeHostedServices)
        {
            sb.AppendLine();
            sb.AppendLine($"Hosted Service Registrations ({hostedServiceRegistrations.Length}):");
            AppendSourceLineMatches(sb, hostedServiceRegistrations, effectiveMaxResults);

            sb.AppendLine();
            sb.AppendLine($"Background Service Implementations ({hostedServiceImplementations.Length}):");
            AppendSourceLineMatches(sb, hostedServiceImplementations, effectiveMaxResults);
        }

        return Task.FromResult(sb.ToString().TrimEnd());
    }

    private static IReadOnlyCollection<ProjectHostInfo> EnumerateProjects(string workspacePath)
    {
        return Directory
            .EnumerateFiles(workspacePath, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !ContainsIgnoredPathSegment(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => ParseProject(path, workspacePath))
            .ToArray();
    }

    private static ProjectHostInfo ParseProject(string projectPath, string workspacePath)
    {
        var document = XDocument.Load(projectPath);
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var projectName = Path.GetFileNameWithoutExtension(projectPath);
        var targetFramework = document
            .Descendants()
            .Where(element => element.Name.LocalName is "TargetFramework" or "TargetFrameworks")
            .Select(element => element.Value.Trim())
            .LastOrDefault();
        var outputType = document
            .Descendants()
            .Where(element => element.Name.LocalName == "OutputType")
            .Select(element => element.Value.Trim())
            .LastOrDefault() ?? "Library";
        var isTestProject = document
            .Descendants()
            .Where(element => element.Name.LocalName == "IsTestProject")
            .Select(element => element.Value.Trim())
            .Any(value => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
        var sdk = document.Root?.Attribute("Sdk")?.Value;

        var programPath = Path.Combine(projectDirectory, "Program.cs");
        var relativeProgramPath = File.Exists(programPath)
            ? Path.GetRelativePath(workspacePath, programPath)
            : null;
        var middlewareCalls = relativeProgramPath == null
            ? Array.Empty<string>()
            : FindMiddlewareCalls(programPath).ToArray();
        var endpointCompositionCalls = relativeProgramPath == null
            ? Array.Empty<string>()
            : FindEndpointCompositionCalls(programPath).ToArray();

        return new ProjectHostInfo(
            projectName,
            Path.GetRelativePath(workspacePath, projectPath),
            InferProjectType(sdk, outputType, projectName, isTestProject, relativeProgramPath != null),
            targetFramework,
            relativeProgramPath,
            middlewareCalls,
            endpointCompositionCalls);
    }

    private static IEnumerable<SourceLineMatch> FindRouteRegistrations(string workspacePath)
    {
        foreach (var filePath in EnumerateSourceFiles(workspacePath))
        {
            foreach (var match in FindLineMatches(workspacePath, filePath, RouteRegex))
                yield return match;
        }
    }

    private static IEnumerable<SourceLineMatch> FindHostedServiceRegistrations(string workspacePath)
    {
        foreach (var filePath in EnumerateSourceFiles(workspacePath))
        {
            foreach (var match in FindLineMatches(workspacePath, filePath, HostedServiceRegistrationRegex))
                yield return match;
        }
    }

    private static IEnumerable<SourceLineMatch> FindBackgroundServiceImplementations(string workspacePath)
    {
        foreach (var filePath in EnumerateSourceFiles(workspacePath))
        {
            foreach (var match in FindLineMatches(workspacePath, filePath, BackgroundServiceImplementationRegex))
                yield return match;
        }
    }

    private static IEnumerable<string> EnumerateSourceFiles(string workspacePath)
    {
        return Directory
            .EnumerateFiles(workspacePath, "*.cs", SearchOption.AllDirectories)
            .Where(path => !ContainsIgnoredPathSegment(path));
    }

    private static IEnumerable<string> FindMiddlewareCalls(string programPath)
        => File.ReadLines(programPath)
            .Select(line => MiddlewareRegex.Match(line))
            .Where(match => match.Success)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> FindEndpointCompositionCalls(string programPath)
        => File.ReadLines(programPath)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("app.Map", StringComparison.Ordinal) && !RouteRegex.IsMatch(line))
            .Select(line => line.TrimEnd(';'))
            .Distinct(StringComparer.Ordinal);

    private static IEnumerable<SourceLineMatch> FindLineMatches(string workspacePath, string filePath, Regex regex)
    {
        var lineNumber = 0;
        foreach (var line in File.ReadLines(filePath))
        {
            lineNumber++;
            if (!regex.IsMatch(line))
                continue;

            yield return new SourceLineMatch(Path.GetRelativePath(workspacePath, filePath), lineNumber, line.Trim());
        }
    }

    private static void AppendSourceLineMatches(
        StringBuilder sb,
        IReadOnlyCollection<SourceLineMatch> matches,
        int maxResults)
    {
        if (matches.Count == 0)
        {
            sb.AppendLine("None.");
            return;
        }

        foreach (var match in matches.Take(maxResults))
            sb.AppendLine($"• {match.RelativePath}:{match.LineNumber} {match.LineText}");

        if (matches.Count > maxResults)
            sb.AppendLine($"... and {matches.Count - maxResults} more");
    }

    private static bool ContainsIgnoredPathSegment(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment => IgnoredPathSegments.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }

    private static string InferProjectType(
        string? sdk,
        string outputType,
        string projectName,
        bool isTestProject,
        bool hasProgram)
    {
        if (isTestProject)
            return "test";

        if (!string.IsNullOrWhiteSpace(sdk) &&
            sdk.Contains("Web", StringComparison.OrdinalIgnoreCase))
        {
            return "web";
        }

        if (string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase))
        {
            if (projectName.Contains("Worker", StringComparison.OrdinalIgnoreCase) ||
                projectName.Contains("Collector", StringComparison.OrdinalIgnoreCase))
            {
                return "worker";
            }

            return "executable";
        }

        return hasProgram ? "host" : "classlib";
    }

    private sealed record ProjectHostInfo(
        string Name,
        string RelativeProjectPath,
        string ProjectType,
        string? TargetFramework,
        string? ProgramPath,
        string[] MiddlewareCalls,
        string[] EndpointCompositionCalls);

    private sealed record SourceLineMatch(
        string RelativePath,
        int LineNumber,
        string LineText);
}
