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
        return await JsonSerializer.DeserializeAsync<WorkspaceGraphSnapshot>(stream, JsonOptions, cancellationToken);
    }

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
