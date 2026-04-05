using System.Text.RegularExpressions;
using System.Xml.Linq;
using CSharpLspMcp.Contracts.Common;
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
    private static readonly Regex LambdaHandlerRegex = new(
        @"\bpublic\s+(?:async\s+)?(?:Task(?:<[^>]+>)?|void)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*Handler)\s*\((?<parameters>[^)]*\bILambdaContext\b[^)]*)\)",
        RegexOptions.Compiled);
    private readonly WorkspaceState _workspaceState;

    public CSharpEntrypointAnalysisService(WorkspaceState workspaceState)
    {
        _workspaceState = workspaceState;
    }

    public Task<EntrypointAnalysisResponse> FindEntrypointsAsync(
        bool includeAspNetRoutes,
        bool includeHostedServices,
        bool includeMiddlewarePipeline,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var workspacePath = _workspaceState.CurrentPath;
        if (string.IsNullOrWhiteSpace(workspacePath))
            throw new InvalidOperationException("Workspace is not set. Call csharp_set_workspace first.");

        cancellationToken.ThrowIfCancellationRequested();

        var effectiveMaxResults = Math.Max(1, maxResults);
        var projects = EnumerateProjects(workspacePath);
        var hostProjects = projects
            .Where(project => project.ProgramPath != null || project.ProjectType is "worker" or "web" or "executable" or "lambda")
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
        var serverlessHandlers = FindServerlessHandlers(workspacePath).ToArray();

        return Task.FromResult(
            new EntrypointAnalysisResponse(
                Summary: $"Found {hostProjects.Length} host project(s), {routeRegistrations.Length} route registration(s), {hostedServiceRegistrations.Length + hostedServiceImplementations.Length} hosted-service surface(s), and {serverlessHandlers.Length} serverless handler(s).",
                SolutionRoot: workspacePath,
                HostProjects: hostProjects
                    .Take(effectiveMaxResults)
                    .Select(project => new HostProjectItem(
                        project.Name,
                        project.ProjectType,
                        project.RelativeProjectPath,
                        project.ProgramPath,
                        includeMiddlewarePipeline ? project.MiddlewareCalls : Array.Empty<string>(),
                        project.EndpointCompositionCalls))
                    .ToArray(),
                AspNetRoutes: includeAspNetRoutes
                    ? routeRegistrations.Take(effectiveMaxResults).Select(MapSourceLineMatch).ToArray()
                    : Array.Empty<SourceLocationItem>(),
                HostedServiceRegistrations: includeHostedServices
                    ? hostedServiceRegistrations.Take(effectiveMaxResults).Select(MapSourceLineMatch).ToArray()
                    : Array.Empty<SourceLocationItem>(),
                BackgroundServiceImplementations: includeHostedServices
                    ? hostedServiceImplementations.Take(effectiveMaxResults).Select(MapSourceLineMatch).ToArray()
                    : Array.Empty<SourceLocationItem>(),
                ServerlessHandlers: serverlessHandlers.Take(effectiveMaxResults).Select(MapSourceLineMatch).ToArray(),
                TruncatedHostProjects: Math.Max(0, hostProjects.Length - effectiveMaxResults),
                TruncatedAspNetRoutes: includeAspNetRoutes ? Math.Max(0, routeRegistrations.Length - effectiveMaxResults) : 0,
                TruncatedHostedServiceRegistrations: includeHostedServices ? Math.Max(0, hostedServiceRegistrations.Length - effectiveMaxResults) : 0,
                TruncatedBackgroundServiceImplementations: includeHostedServices ? Math.Max(0, hostedServiceImplementations.Length - effectiveMaxResults) : 0,
                TruncatedServerlessHandlers: Math.Max(0, serverlessHandlers.Length - effectiveMaxResults)));
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

    private static IEnumerable<SourceLineMatch> FindServerlessHandlers(string workspacePath)
    {
        foreach (var filePath in EnumerateSourceFiles(workspacePath))
        {
            foreach (var match in FindLineMatches(workspacePath, filePath, LambdaHandlerRegex))
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

    private static SourceLocationItem MapSourceLineMatch(SourceLineMatch match)
        => new(match.RelativePath, match.LineNumber, match.LineText);

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

        if (projectName.Contains("Lambda", StringComparison.OrdinalIgnoreCase))
            return "lambda";

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

    public sealed record EntrypointAnalysisResponse(
        string Summary,
        string SolutionRoot,
        HostProjectItem[] HostProjects,
        SourceLocationItem[] AspNetRoutes,
        SourceLocationItem[] HostedServiceRegistrations,
        SourceLocationItem[] BackgroundServiceImplementations,
        SourceLocationItem[] ServerlessHandlers,
        int TruncatedHostProjects,
        int TruncatedAspNetRoutes,
        int TruncatedHostedServiceRegistrations,
        int TruncatedBackgroundServiceImplementations,
        int TruncatedServerlessHandlers) : IStructuredToolResult;

    public sealed record HostProjectItem(
        string Name,
        string ProjectType,
        string ProjectPath,
        string? ProgramPath,
        string[] MiddlewareCalls,
        string[] EndpointCompositionCalls);

    public sealed record SourceLocationItem(
        string RelativePath,
        int LineNumber,
        string Text);
}
