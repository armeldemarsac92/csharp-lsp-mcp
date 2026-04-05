using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;

namespace CSharpLspMcp.Workspace.Roslyn;

public sealed class CSharpRoslynWorkspaceHost
{
    private static readonly object RegistrationLock = new();
    private static bool _msBuildRegistered;
    private readonly ILogger<CSharpRoslynWorkspaceHost> _logger;

    public CSharpRoslynWorkspaceHost(ILogger<CSharpRoslynWorkspaceHost> logger)
    {
        _logger = logger;
    }

    public async Task<RoslynWorkspaceContext> OpenAsync(string workspacePath, CancellationToken cancellationToken)
    {
        EnsureMsBuildRegistered();

        var normalizedWorkspaceRoot = NormalizeWorkspaceRoot(workspacePath);
        var targetPath = ResolveWorkspaceTargetPath(workspacePath);
        var workspace = MSBuildWorkspace.Create();
        var warnings = new List<string>();
        workspace.WorkspaceFailed += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.Diagnostic.Message))
                return;

            warnings.Add(args.Diagnostic.Message);
            _logger.LogWarning("Roslyn workspace diagnostic: {Message}", args.Diagnostic.Message);
        };

        Solution solution;
        if (targetPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            var project = await workspace.OpenProjectAsync(targetPath, cancellationToken: cancellationToken);
            solution = project.Solution;
        }
        else
        {
            solution = await workspace.OpenSolutionAsync(targetPath, cancellationToken: cancellationToken);
        }

        return new RoslynWorkspaceContext(
            normalizedWorkspaceRoot,
            targetPath,
            solution,
            warnings,
            workspace);
    }

    internal static string ResolveWorkspaceTargetPath(string workspacePath)
    {
        var fullPath = Path.GetFullPath(workspacePath);
        if (File.Exists(fullPath))
        {
            return Path.GetExtension(fullPath).ToLowerInvariant() switch
            {
                ".sln" => fullPath,
                ".slnx" => fullPath,
                ".csproj" => fullPath,
                _ => throw new InvalidOperationException($"Unsupported workspace target: {fullPath}")
            };
        }

        if (!Directory.Exists(fullPath))
            throw new InvalidOperationException($"Workspace path does not exist: {workspacePath}");

        var directory = new DirectoryInfo(fullPath);
        var preferredTarget = EnumerateWorkspaceTargets(directory).FirstOrDefault();
        if (preferredTarget == null)
            throw new InvalidOperationException($"No .sln, .slnx, or .csproj file found under: {workspacePath}");

        return preferredTarget.FullName;
    }

    private static string NormalizeWorkspaceRoot(string workspacePath)
    {
        var fullPath = Path.GetFullPath(workspacePath);
        return File.Exists(fullPath)
            ? Path.GetDirectoryName(fullPath) ?? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static IEnumerable<FileInfo> EnumerateWorkspaceTargets(DirectoryInfo directory)
    {
        foreach (var extension in new[] { "*.slnx", "*.sln", "*.csproj" })
        {
            foreach (var candidate in directory.EnumerateFiles(extension, SearchOption.TopDirectoryOnly)
                         .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase))
            {
                yield return candidate;
            }
        }
    }

    private static void EnsureMsBuildRegistered()
    {
        if (_msBuildRegistered)
            return;

        lock (RegistrationLock)
        {
            if (_msBuildRegistered)
                return;

            MSBuildLocator.RegisterDefaults();
            _msBuildRegistered = true;
        }
    }
}

public sealed class RoslynWorkspaceContext : IDisposable
{
    private readonly MSBuildWorkspace _workspace;

    public RoslynWorkspaceContext(
        string workspaceRoot,
        string targetPath,
        Solution solution,
        IReadOnlyCollection<string> warnings,
        MSBuildWorkspace workspace)
    {
        WorkspaceRoot = workspaceRoot;
        TargetPath = targetPath;
        Solution = solution;
        Warnings = warnings;
        _workspace = workspace;
    }

    public string WorkspaceRoot { get; }

    public string TargetPath { get; }

    public Solution Solution { get; }

    public IReadOnlyCollection<string> Warnings { get; }

    public void Dispose()
    {
        _workspace.Dispose();
    }
}
