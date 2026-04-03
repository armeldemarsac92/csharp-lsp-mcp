using CSharpLspMcp.Lsp;
using Microsoft.Extensions.Logging;

namespace CSharpLspMcp.Workspace;

public sealed class CSharpWorkspaceSession
{
    private static readonly TimeSpan WorkspaceReadyTimeout = TimeSpan.FromSeconds(30);
    private readonly ILogger<CSharpWorkspaceSession> _logger;
    private readonly LspClient _lspClient;
    private readonly WorkspaceState _workspaceState;
    private readonly Dictionary<string, OpenDocumentState> _openDocuments = new();

    public CSharpWorkspaceSession(
        ILogger<CSharpWorkspaceSession> logger,
        LspClient lspClient,
        WorkspaceState workspaceState)
    {
        _logger = logger;
        _lspClient = lspClient;
        _workspaceState = workspaceState;
    }

    public bool IsRunning => _lspClient.IsRunning;

    public string? WorkspacePath => _workspaceState.CurrentPath;

    public async Task<bool> SetWorkspaceAsync(string path, CancellationToken cancellationToken)
    {
        if (_lspClient.IsRunning &&
            WorkspacePath != null &&
            !string.Equals(WorkspacePath, path, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Workspace changed from {Old} to {New}, restarting LSP",
                WorkspacePath,
                path);
            await _lspClient.StopAsync();
            _openDocuments.Clear();
        }

        _workspaceState.SetPath(path);
        var started = await _lspClient.StartAsync(path, cancellationToken);
        if (started)
            await _lspClient.WaitForWorkspaceReadyAsync(WorkspaceReadyTimeout, cancellationToken);

        return started;
    }

    public async Task StopAsync()
    {
        await _lspClient.StopAsync();
        _openDocuments.Clear();
        _workspaceState.SetPath(null);
    }

    public string GetAbsolutePath(string path)
    {
        if (Path.IsPathRooted(path))
            return path;

        if (WorkspacePath != null)
            return Path.Combine(WorkspacePath, path);

        return Path.GetFullPath(path);
    }

    public PublishDiagnosticsParams? GetCachedDiagnostics(string filePath)
        => _lspClient.GetCachedDiagnostics(filePath);

    public async Task EnsureDocumentOpenAsync(string filePath, string? content, CancellationToken cancellationToken)
    {
        if (WorkspacePath == null)
            await EnsureWorkspaceForFileAsync(filePath, cancellationToken);

        var effectiveContent = ShouldReadContentFromDisk(content)
            ? await File.ReadAllTextAsync(filePath, cancellationToken)
            : content!;

        if (_openDocuments.TryGetValue(filePath, out var state))
        {
            if (state.Content != effectiveContent)
            {
                state.Content = effectiveContent;
                state.Version++;
                await _lspClient.UpdateDocumentAsync(filePath, effectiveContent, state.Version, cancellationToken);
            }

            return;
        }

        await _lspClient.OpenDocumentAsync(filePath, effectiveContent, cancellationToken);
        _openDocuments[filePath] = new OpenDocumentState
        {
            Content = effectiveContent,
            Version = 1
        };
    }

    private async Task EnsureWorkspaceForFileAsync(string filePath, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(filePath);
        while (dir != null)
        {
            if (ContainsWorkspaceMarker(dir))
            {
                _workspaceState.SetPath(dir);
                var started = await _lspClient.StartAsync(dir, cancellationToken);
                if (started)
                    await _lspClient.WaitForWorkspaceReadyAsync(WorkspaceReadyTimeout, cancellationToken);
                return;
            }

            dir = Path.GetDirectoryName(dir);
        }

        var fileDirectory = Path.GetDirectoryName(filePath) ?? Directory.GetCurrentDirectory();
        _workspaceState.SetPath(fileDirectory);
        var fileDirectoryStarted = await _lspClient.StartAsync(fileDirectory, cancellationToken);
        if (fileDirectoryStarted)
            await _lspClient.WaitForWorkspaceReadyAsync(WorkspaceReadyTimeout, cancellationToken);
    }

    private static bool ContainsWorkspaceMarker(string dir)
        => Directory.GetFiles(dir, "*.sln").Any() ||
           Directory.GetFiles(dir, "*.slnx").Any() ||
           Directory.GetFiles(dir, "*.csproj").Any();

    private static bool ShouldReadContentFromDisk(string? content)
        => string.IsNullOrEmpty(content);

    private sealed class OpenDocumentState
    {
        public required string Content { get; set; }

        public int Version { get; set; }
    }
}
