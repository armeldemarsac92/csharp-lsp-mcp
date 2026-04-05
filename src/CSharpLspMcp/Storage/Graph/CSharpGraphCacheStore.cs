using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSharpLspMcp.Storage.Graph;

public sealed class CSharpGraphCacheStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public string GetStoragePath(string workspacePath)
    {
        var storageDirectory = GetStorageDirectory();
        Directory.CreateDirectory(storageDirectory);

        return Path.Combine(storageDirectory, $"{ComputeWorkspaceKey(workspacePath)}.graph.json");
    }

    public async Task SaveAsync(WorkspaceGraphSnapshot snapshot, CancellationToken cancellationToken)
    {
        var storagePath = GetStoragePath(snapshot.WorkspaceRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(storagePath)!);

        await using var stream = File.Create(storagePath);
        await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken);
    }

    public async Task<WorkspaceGraphSnapshot?> LoadAsync(string workspacePath, CancellationToken cancellationToken)
    {
        var storagePath = GetStoragePath(workspacePath);
        if (!File.Exists(storagePath))
            return null;

        await using var stream = File.OpenRead(storagePath);
        var snapshot = await JsonSerializer.DeserializeAsync<WorkspaceGraphSnapshot>(stream, JsonOptions, cancellationToken);
        return snapshot == null ? null : Normalize(snapshot);
    }

    private static WorkspaceGraphSnapshot Normalize(WorkspaceGraphSnapshot snapshot)
        => snapshot with
        {
            NodeCounts = snapshot.NodeCounts ?? Array.Empty<WorkspaceGraphCountItem>(),
            EdgeCounts = snapshot.EdgeCounts ?? Array.Empty<WorkspaceGraphCountItem>(),
            Projects = snapshot.Projects ?? Array.Empty<WorkspaceGraphProjectSummary>(),
            ProjectStates = snapshot.ProjectStates ?? Array.Empty<WorkspaceGraphProjectState>(),
            Nodes = snapshot.Nodes ?? Array.Empty<WorkspaceGraphNode>(),
            Edges = snapshot.Edges ?? Array.Empty<WorkspaceGraphEdge>(),
            Features = snapshot.Features ?? Array.Empty<string>(),
            Warnings = snapshot.Warnings ?? Array.Empty<string>()
        };

    internal static string GetStorageDirectory()
    {
        var baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDirectory))
            baseDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

        return Path.Combine(baseDirectory, "csharp-lsp-mcp", "graphs");
    }

    internal static string ComputeWorkspaceKey(string workspacePath)
    {
        var normalizedPath = Path.GetFullPath(workspacePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
