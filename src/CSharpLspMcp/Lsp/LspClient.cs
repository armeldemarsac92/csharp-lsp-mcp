using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace CSharpLspMcp.Lsp;

public class LspClient : IAsyncDisposable
{
    private readonly ILogger<LspClient> _logger;
    private readonly SolutionFilter _solutionFilter;
    private Process? _lspProcess;
    private Stream? _outputStream;
    private int _requestId;
    private readonly ConcurrentDictionary<int, PendingRequest> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, PublishDiagnosticsParams> _diagnosticsCache = new();
    private CancellationTokenSource? _readLoopCts;
    private Task? _readLoopTask;
    private bool _isInitialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TaskCompletionSource<bool>? _workspaceReadySource;
    private ServerCapabilities? _serverCapabilities;
    private string? _filteredWorkspacePath;
    private string? _workspacePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public event Action<PublishDiagnosticsParams>? DiagnosticsReceived;

    private sealed record PendingRequest(
        string Method,
        DateTimeOffset StartedAt,
        TaskCompletionSource<JsonElement> CompletionSource);

    public LspClient(ILogger<LspClient> logger, SolutionFilter solutionFilter)
    {
        _logger = logger;
        _solutionFilter = solutionFilter;
    }

    public bool IsRunning => _isInitialized && _lspProcess != null && !_lspProcess.HasExited;

    /// <summary>
    /// Stops the LSP server and resets state so it can be restarted with StartAsync.
    /// </summary>
    public async Task StopAsync()
    {
        await _initLock.WaitAsync();
        try
        {
            if (!_isInitialized && _lspProcess == null)
                return;

            _logger.LogInformation("Stopping LSP server...");
            var readLoopCts = _readLoopCts;
            var readLoopTask = _readLoopTask;

            if (_lspProcess != null && !_lspProcess.HasExited)
            {
                try
                {
                    await SendRequestAsync<object>("shutdown", null, CancellationToken.None);
                    await SendNotificationAsync("exit", null, CancellationToken.None);

                    if (!_lspProcess.WaitForExit(3000))
                        _lspProcess.Kill();
                }
                catch
                {
                    try { _lspProcess.Kill(); } catch { }
                }
            }

            readLoopCts?.Cancel();

            if (readLoopTask != null)
            {
                try
                {
                    await readLoopTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch { }
            }

            _lspProcess?.Dispose();
            _readLoopCts?.Dispose();

            _lspProcess = null;
            _outputStream = null;
            _readLoopCts = null;
            _readLoopTask = null;
            _isInitialized = false;
            _serverCapabilities = null;
            _filteredWorkspacePath = null;
            _workspaceReadySource = null;
            _workspacePath = null;
            _requestId = 0;
            _pendingRequests.Clear();
            _diagnosticsCache.Clear();

            _solutionFilter.Cleanup();

            _logger.LogInformation("LSP server stopped.");
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<bool> StartAsync(string? workspacePath = null, CancellationToken cancellationToken = default)
    {
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_isInitialized)
                return true;

            _workspacePath = workspacePath;

            // Try to find csharp-ls
            var lspPath = await FindLspServerAsync(cancellationToken);
            if (lspPath == null)
            {
                _logger.LogError("Could not find csharp-ls. Install it with: dotnet tool install --global csharp-ls");
                return false;
            }

            string? effectiveWorkspacePath = workspacePath;
            string? launchWorkingDirectory = workspacePath;
            string? launchSolutionPath = null;

            // Resolve the specific workspace context we should launch csharp-ls against.
            if (workspacePath != null)
            {
                var launchContext = _solutionFilter.ResolveWorkspaceLaunchContext(workspacePath);
                launchWorkingDirectory = launchContext.WorkingDirectory;
                launchSolutionPath = launchContext.SolutionPath;
                _filteredWorkspacePath = !string.Equals(launchWorkingDirectory, workspacePath, StringComparison.Ordinal)
                    ? launchWorkingDirectory
                    : null;

                if (_filteredWorkspacePath != null)
                {
                    _logger.LogInformation("Using filtered workspace: {Path}", _filteredWorkspacePath);
                }

                if (!string.IsNullOrWhiteSpace(launchSolutionPath))
                    _logger.LogInformation("Launching csharp-ls with solution: {Path}", launchSolutionPath);
            }

            _logger.LogInformation("Starting LSP server: {Path}", lspPath);

            var startInfo = CreateStartInfo(lspPath, launchWorkingDirectory, launchSolutionPath);

            _lspProcess = new Process { StartInfo = startInfo };
            _lspProcess.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    _logger.LogWarning("LSP stderr: {Message}", e.Data);
            };

            if (!_lspProcess.Start())
            {
                _logger.LogError("Failed to start LSP process");
                return false;
            }

            _logger.LogDebug("LSP process started, PID: {Pid}", _lspProcess.Id);
            _lspProcess.BeginErrorReadLine();

            // Small delay to let the process start
            await Task.Delay(100, cancellationToken);

            if (_lspProcess.HasExited)
            {
                _logger.LogError("LSP process exited immediately with code: {ExitCode}", _lspProcess.ExitCode);
                return false;
            }
            _outputStream = _lspProcess.StandardOutput.BaseStream;

            _readLoopCts = new CancellationTokenSource();
            _readLoopTask = Task.Run(() => ReadLoopAsync(_readLoopCts.Token), _readLoopCts.Token);
            _workspaceReadySource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Initialize the LSP server with the (potentially filtered) workspace
            var initResult = await InitializeAsync(effectiveWorkspacePath, cancellationToken);
            if (initResult == null)
            {
                _logger.LogError("LSP initialization failed");
                await CleanupFailedStartAsync();
                return false;
            }

            _logger.LogInformation("LSP server initialized: {ServerName} {Version}",
                initResult.ServerInfo?.Name ?? "Unknown",
                initResult.ServerInfo?.Version ?? "Unknown");

            // Send initialized notification
            await SendNotificationAsync("initialized", new { }, cancellationToken);

            if (string.IsNullOrWhiteSpace(launchSolutionPath))
                _workspaceReadySource.TrySetResult(true);

            _serverCapabilities = initResult.Capabilities;
            _isInitialized = true;
            return true;
        }
        catch
        {
            await CleanupFailedStartAsync();
            throw;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<string?> FindLspServerAsync(CancellationToken cancellationToken)
    {
        // Check common locations for csharp-ls
        var isWindows = OperatingSystem.IsWindows();
        var exeExtension = isWindows ? ".exe" : "";

        var possiblePaths = new[]
        {
            "csharp-ls", // In PATH (Windows handles .exe automatically)
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", $"csharp-ls{exeExtension}"),
            "/usr/local/bin/csharp-ls",
            "/usr/bin/csharp-ls"
        };

        foreach (var path in possiblePaths)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process != null)
                {
                    await process.WaitForExitAsync(cancellationToken);
                    if (process.ExitCode == 0)
                        return path;
                }
            }
            catch
            {
                // Try next path
            }
        }

        return null;
    }

    private static ProcessStartInfo CreateStartInfo(string lspPath, string? workingDirectory, string? solutionPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = lspPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Don't set encoding - we write bytes directly to avoid BOM issues
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
            startInfo.WorkingDirectory = workingDirectory;

        if (!string.IsNullOrWhiteSpace(solutionPath))
        {
            startInfo.ArgumentList.Add("--solution");
            startInfo.ArgumentList.Add(
                !string.IsNullOrWhiteSpace(startInfo.WorkingDirectory)
                    ? Path.GetRelativePath(startInfo.WorkingDirectory, solutionPath)
                    : solutionPath);
        }

        return startInfo;
    }

    private async Task<InitializeResult?> InitializeAsync(string? workspacePath, CancellationToken cancellationToken)
    {
        _logger.LogInformation("InitializeAsync: Sending initialize request to LSP server...");
        var rootUri = workspacePath != null ? new Uri(workspacePath).ToString() : null;

        var initParams = new InitializeParams
        {
            ProcessId = Environment.ProcessId,
            RootUri = rootUri,
            RootPath = workspacePath,
            Capabilities = new ClientCapabilities
            {
                TextDocument = new TextDocumentClientCapabilities
                {
                    Synchronization = new TextDocumentSyncClientCapabilities
                    {
                        DynamicRegistration = false,
                        WillSave = false,
                        DidSave = true
                    },
                    Completion = new CompletionClientCapabilities
                    {
                        DynamicRegistration = false,
                        CompletionItem = new CompletionItemCapabilities
                        {
                            SnippetSupport = true,
                            DocumentationFormat = new[] { "markdown", "plaintext" }
                        }
                    },
                    Hover = new HoverClientCapabilities
                    {
                        DynamicRegistration = false,
                        ContentFormat = new[] { "markdown", "plaintext" }
                    },
                    Implementation = new DynamicRegistrationClientCapabilities
                    {
                        DynamicRegistration = false
                    },
                    CallHierarchy = new DynamicRegistrationClientCapabilities
                    {
                        DynamicRegistration = false
                    },
                    TypeHierarchy = new DynamicRegistrationClientCapabilities
                    {
                        DynamicRegistration = false
                    },
                    Diagnostic = new DiagnosticClientCapabilities
                    {
                        DynamicRegistration = false,
                        RelatedDocumentSupport = false
                    },
                    PublishDiagnostics = new PublishDiagnosticsClientCapabilities
                    {
                        RelatedInformation = true
                    }
                },
                Workspace = new WorkspaceClientCapabilities
                {
                    WorkspaceFolders = true,
                    Symbol = new WorkspaceSymbolClientCapabilities
                    {
                        DynamicRegistration = false
                    },
                    Diagnostics = new DiagnosticWorkspaceClientCapabilities
                    {
                        RefreshSupport = false
                    }
                }
            },
            WorkspaceFolders = workspacePath != null
                ? new[] { new WorkspaceFolder { Uri = rootUri!, Name = Path.GetFileName(workspacePath) } }
                : null
        };

        _logger.LogDebug("InitializeAsync: Workspace folders: {Folders}", workspacePath);
        var response = await SendRequestAsync<InitializeResult>("initialize", initParams, cancellationToken);
        _logger.LogInformation("InitializeAsync: Received response from LSP server");
        return response;
    }

    public async Task OpenDocumentAsync(string filePath, string content, CancellationToken cancellationToken = default)
    {
        var uri = new Uri(filePath).ToString();
        var param = new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                Uri = uri,
                LanguageId = "csharp",
                Version = 1,
                Text = content
            }
        };

        await SendNotificationAsync("textDocument/didOpen", param, cancellationToken);
        _logger.LogDebug("Opened document: {Uri}", uri);
    }

    public async Task UpdateDocumentAsync(string filePath, string content, int version, CancellationToken cancellationToken = default)
    {
        var uri = new Uri(filePath).ToString();
        var param = new DidChangeTextDocumentParams
        {
            TextDocument = new VersionedTextDocumentIdentifier
            {
                Uri = uri,
                Version = version
            },
            ContentChanges = new[] { new TextDocumentContentChangeEvent { Text = content } }
        };

        await SendNotificationAsync("textDocument/didChange", param, cancellationToken);
        _logger.LogDebug("Updated document: {Uri} (version {Version})", uri, version);
    }

    public async Task CloseDocumentAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var uri = new Uri(filePath).ToString();
        var param = new DidCloseTextDocumentParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri }
        };

        await SendNotificationAsync("textDocument/didClose", param, cancellationToken);
        _diagnosticsCache.TryRemove(uri, out _);
        _logger.LogDebug("Closed document: {Uri}", uri);
    }

    public PublishDiagnosticsParams? GetCachedDiagnostics(string filePath)
    {
        var uri = new Uri(filePath).ToString();
        _diagnosticsCache.TryGetValue(uri, out var diagnostics);
        return diagnostics;
    }

    public async Task<PublishDiagnosticsParams?> WaitForDiagnosticsAsync(string filePath, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var uri = new Uri(filePath).ToString();
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (_diagnosticsCache.TryGetValue(uri, out var diagnostics))
                return diagnostics;

            await Task.Delay(100, cancellationToken);
        }

        return GetCachedDiagnostics(filePath);
    }

    public async Task<Hover?> GetHoverAsync(string filePath, int line, int character, CancellationToken cancellationToken = default)
    {
        var param = new HoverParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = new Uri(filePath).ToString() },
            Position = new Position { Line = line, Character = character }
        };

        return await SendRequestAsync<Hover>("textDocument/hover", param, cancellationToken);
    }

    public async Task<CompletionItem[]?> GetCompletionsAsync(string filePath, int line, int character, CancellationToken cancellationToken = default)
    {
        var param = new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = new Uri(filePath).ToString() },
            Position = new Position { Line = line, Character = character },
            Context = new CompletionContext { TriggerKind = CompletionTriggerKind.Invoked }
        };

        var result = await SendRequestAsync<JsonElement>("textDocument/completion", param, cancellationToken);
        if (result.ValueKind == JsonValueKind.Undefined || result.ValueKind == JsonValueKind.Null)
            return null;

        // Response can be CompletionItem[] or CompletionList
        if (result.ValueKind == JsonValueKind.Array)
            return result.Deserialize<CompletionItem[]>(JsonOptions);

        var list = result.Deserialize<CompletionList>(JsonOptions);
        return list?.Items;
    }

    public async Task<Location[]?> GetDefinitionAsync(string filePath, int line, int character, CancellationToken cancellationToken = default)
    {
        var param = new DefinitionParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = new Uri(filePath).ToString() },
            Position = new Position { Line = line, Character = character }
        };

        var result = await SendRequestAsync<JsonElement>("textDocument/definition", param, cancellationToken);
        return ParseLocationArrayResult(result);
    }

    public async Task<Location[]?> GetReferencesAsync(string filePath, int line, int character, bool includeDeclaration = true, CancellationToken cancellationToken = default)
    {
        var param = new ReferenceParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = new Uri(filePath).ToString() },
            Position = new Position { Line = line, Character = character },
            Context = new ReferenceContext { IncludeDeclaration = includeDeclaration }
        };

        return await SendRequestAsync<Location[]>("textDocument/references", param, cancellationToken);
    }

    public async Task<SymbolInformation[]?> SearchWorkspaceSymbolsAsync(string query, CancellationToken cancellationToken = default)
    {
        EnsureServerCapability("workspace/symbol", _serverCapabilities?.WorkspaceSymbolProvider);

        var param = new WorkspaceSymbolParams
        {
            Query = query
        };

        return await SendRequestAsync<SymbolInformation[]>("workspace/symbol", param, cancellationToken);
    }

    public async Task<Location[]?> GetImplementationsAsync(string filePath, int line, int character, CancellationToken cancellationToken = default)
    {
        EnsureServerCapability("textDocument/implementation", _serverCapabilities?.ImplementationProvider);

        var param = new ImplementationParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = new Uri(filePath).ToString() },
            Position = new Position { Line = line, Character = character }
        };

        var result = await SendRequestAsync<JsonElement>("textDocument/implementation", param, cancellationToken);
        return ParseLocationArrayResult(result);
    }

    public async Task<CallHierarchyItem[]?> PrepareCallHierarchyAsync(string filePath, int line, int character, CancellationToken cancellationToken = default)
    {
        EnsureServerCapability("textDocument/prepareCallHierarchy", _serverCapabilities?.CallHierarchyProvider);

        var param = new CallHierarchyPrepareParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = new Uri(filePath).ToString() },
            Position = new Position { Line = line, Character = character }
        };

        return await SendRequestAsync<CallHierarchyItem[]>("textDocument/prepareCallHierarchy", param, cancellationToken);
    }

    public async Task<CallHierarchyIncomingCall[]?> GetIncomingCallsAsync(CallHierarchyItem item, CancellationToken cancellationToken = default)
    {
        EnsureServerCapability("callHierarchy/incomingCalls", _serverCapabilities?.CallHierarchyProvider);

        var param = new CallHierarchyIncomingCallsParams
        {
            Item = item
        };

        return await SendRequestAsync<CallHierarchyIncomingCall[]>("callHierarchy/incomingCalls", param, cancellationToken);
    }

    public async Task<CallHierarchyOutgoingCall[]?> GetOutgoingCallsAsync(CallHierarchyItem item, CancellationToken cancellationToken = default)
    {
        EnsureServerCapability("callHierarchy/outgoingCalls", _serverCapabilities?.CallHierarchyProvider);

        var param = new CallHierarchyOutgoingCallsParams
        {
            Item = item
        };

        return await SendRequestAsync<CallHierarchyOutgoingCall[]>("callHierarchy/outgoingCalls", param, cancellationToken);
    }

    public async Task<TypeHierarchyItem[]?> PrepareTypeHierarchyAsync(string filePath, int line, int character, CancellationToken cancellationToken = default)
    {
        EnsureServerCapability("textDocument/prepareTypeHierarchy", _serverCapabilities?.TypeHierarchyProvider);

        var param = new TypeHierarchyPrepareParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = new Uri(filePath).ToString() },
            Position = new Position { Line = line, Character = character }
        };

        return await SendRequestAsync<TypeHierarchyItem[]>("textDocument/prepareTypeHierarchy", param, cancellationToken);
    }

    public async Task<TypeHierarchyItem[]?> GetTypeHierarchySupertypesAsync(TypeHierarchyItem item, CancellationToken cancellationToken = default)
    {
        EnsureServerCapability("typeHierarchy/supertypes", _serverCapabilities?.TypeHierarchyProvider);

        var param = new TypeHierarchySupertypesParams
        {
            Item = item
        };

        return await SendRequestAsync<TypeHierarchyItem[]>("typeHierarchy/supertypes", param, cancellationToken);
    }

    public async Task<TypeHierarchyItem[]?> GetTypeHierarchySubtypesAsync(TypeHierarchyItem item, CancellationToken cancellationToken = default)
    {
        EnsureServerCapability("typeHierarchy/subtypes", _serverCapabilities?.TypeHierarchyProvider);

        var param = new TypeHierarchySubtypesParams
        {
            Item = item
        };

        return await SendRequestAsync<TypeHierarchyItem[]>("typeHierarchy/subtypes", param, cancellationToken);
    }

    public async Task<WorkspaceDiagnosticReport?> GetWorkspaceDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        EnsureServerCapability("workspace/diagnostic", _serverCapabilities?.DiagnosticProvider);

        var param = new WorkspaceDiagnosticParams
        {
            PreviousResultIds = Array.Empty<PreviousResultId>()
        };

        return await SendRequestAsync<WorkspaceDiagnosticReport>("workspace/diagnostic", param, cancellationToken);
    }

    public async Task WaitForWorkspaceReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var readySource = _workspaceReadySource;
        if (readySource == null)
            return;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await readySource.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Timed out waiting {TimeoutSeconds}s for workspace readiness signal.",
                timeout.TotalSeconds);
        }
    }

    public async Task<object?> GetDocumentSymbolsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var param = new DocumentSymbolParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = new Uri(filePath).ToString() }
        };

        var result = await SendRequestAsync<JsonElement>("textDocument/documentSymbol", param, cancellationToken);
        if (result.ValueKind == JsonValueKind.Undefined || result.ValueKind == JsonValueKind.Null)
            return null;

        // Try to determine if it's DocumentSymbol[] or SymbolInformation[]
        if (result.ValueKind == JsonValueKind.Array && result.GetArrayLength() > 0)
        {
            var first = result[0];
            if (first.TryGetProperty("selectionRange", out _))
                return result.Deserialize<DocumentSymbol[]>(JsonOptions);
            else
                return result.Deserialize<SymbolInformation[]>(JsonOptions);
        }

        return Array.Empty<DocumentSymbol>();
    }

    public async Task<CodeAction[]?> GetCodeActionsAsync(string filePath, Range range, Diagnostic[] diagnostics, CancellationToken cancellationToken = default)
    {
        var param = new CodeActionParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = new Uri(filePath).ToString() },
            Range = range,
            Context = new CodeActionContext { Diagnostics = diagnostics }
        };

        return await SendRequestAsync<CodeAction[]>("textDocument/codeAction", param, cancellationToken);
    }

    public async Task<WorkspaceEdit?> RenameSymbolAsync(string filePath, int line, int character, string newName, CancellationToken cancellationToken = default)
    {
        var param = new RenameParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = new Uri(filePath).ToString() },
            Position = new Position { Line = line, Character = character },
            NewName = newName
        };

        return await SendRequestAsync<WorkspaceEdit>("textDocument/rename", param, cancellationToken);
    }

    private async Task<T?> SendRequestAsync<T>(string method, object? @params, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _requestId);
        var request = new JsonRpcRequest
        {
            Id = id,
            Method = method,
            Params = @params
        };

        var pendingRequest = new PendingRequest(
            method,
            DateTimeOffset.UtcNow,
            new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously));
        _pendingRequests[id] = pendingRequest;

        try
        {
            _logger.LogDebug("SendRequestAsync: Sending request id={Id} method={Method}", id, method);
            await SendMessageAsync(request, cancellationToken);
            _logger.LogDebug("SendRequestAsync: Request sent, waiting for response...");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(120));

            var result = await pendingRequest.CompletionSource.Task.WaitAsync(cts.Token);
            _logger.LogDebug(
                "SendRequestAsync: Completed request id={Id} method={Method} after {ElapsedMs}ms",
                id,
                method,
                (DateTimeOffset.UtcNow - pendingRequest.StartedAt).TotalMilliseconds);

            if (result.ValueKind == JsonValueKind.Null || result.ValueKind == JsonValueKind.Undefined)
                return default;

            return result.Deserialize<T>(JsonOptions);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("SendRequestAsync: Request id={Id} method={Method} was cancelled by caller", id, method);
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "SendRequestAsync: Request id={Id} method={Method} timed out after waiting {ElapsedMs}ms. Pending requests: {PendingCount}",
                id,
                method,
                (DateTimeOffset.UtcNow - pendingRequest.StartedAt).TotalMilliseconds,
                _pendingRequests.Count);
            throw;
        }
        finally
        {
            _pendingRequests.TryRemove(id, out _);
        }
    }

    private async Task SendNotificationAsync(string method, object? @params, CancellationToken cancellationToken)
    {
        var notification = new JsonRpcNotification
        {
            Method = method,
            Params = @params
        };

        await SendMessageAsync(notification, cancellationToken);
    }

    private async Task SendMessageAsync(object message, CancellationToken cancellationToken)
    {
        if (_lspProcess?.StandardInput?.BaseStream == null)
            throw new InvalidOperationException("LSP client not started");

        var json = JsonSerializer.Serialize(message, JsonOptions);
        // Write bytes directly to avoid encoding issues (BOM, etc.)
        var content = $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n{json}";
        var bytes = Encoding.UTF8.GetBytes(content);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _lspProcess.StandardInput.BaseStream.WriteAsync(bytes, cancellationToken);
            await _lspProcess.StandardInput.BaseStream.FlushAsync(cancellationToken);
            _logger.LogTrace("Sent: {Message}", json);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("ReadLoopAsync: Starting read loop");
        if (_outputStream == null)
        {
            _logger.LogError("ReadLoopAsync: Output stream is null!");
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogDebug("ReadLoopAsync: Waiting for content length header...");
                var contentLength = await ReadContentLengthAsync(_outputStream, cancellationToken);
                if (contentLength == null)
                {
                    _logger.LogWarning("ReadLoopAsync: Stream closed (null content length)");
                    return; // Stream closed
                }

                _logger.LogDebug("ReadLoopAsync: Content-Length: {Length}", contentLength.Value);
                if (contentLength.Value <= 0)
                    continue;

                var payload = new byte[contentLength.Value];
                var readOk = await ReadExactAsync(_outputStream, payload, payload.Length, cancellationToken);
                if (!readOk)
                {
                    _logger.LogWarning("ReadLoopAsync: Stream closed while reading payload");
                    return; // Stream closed
                }

                var json = Encoding.UTF8.GetString(payload);
                _logger.LogTrace("Received: {Message}", json);
                _logger.LogDebug("ReadLoopAsync: Received message, length={Length}", json.Length);

                await ProcessMessageAsync(json, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("ReadLoopAsync: Cancelled");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in LSP read loop");
            }
        }
        _logger.LogDebug("ReadLoopAsync: Exiting read loop");
    }

    private async Task ProcessMessageAsync(string json, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("method", out var methodElement))
            {
                var method = methodElement.GetString();

                if (root.TryGetProperty("id", out var requestIdElement))
                {
                    await HandleServerRequestAsync(
                        requestIdElement.Clone(),
                        method,
                        root.TryGetProperty("params", out var requestParams) ? requestParams.Clone() : (JsonElement?)null,
                        cancellationToken);
                    return;
                }

                HandleNotification(method, root);
                return;
            }

            if (root.TryGetProperty("id", out var idElement))
            {
                if (!TryGetRequestId(idElement, out var id))
                {
                    _logger.LogWarning("Received response with unsupported id payload: {Id}", idElement.GetRawText());
                    return;
                }

                if (!_pendingRequests.TryGetValue(id, out var pendingRequest))
                {
                    _logger.LogDebug("Received response id={Id} but no pending request matched it", id);
                    return;
                }

                _logger.LogDebug("Received response id={Id} for method={Method}", id, pendingRequest.Method);

                if (root.TryGetProperty("error", out var error))
                {
                    var errorMsg = error.GetProperty("message").GetString();
                    _logger.LogError("LSP error for request id={Id} method={Method}: {Error}", id, pendingRequest.Method, errorMsg);
                    pendingRequest.CompletionSource.TrySetException(new Exception($"LSP error: {errorMsg}"));
                }
                else if (root.TryGetProperty("result", out var result))
                {
                    pendingRequest.CompletionSource.TrySetResult(result.Clone());
                    _logger.LogDebug("Resolved request id={Id} method={Method}", id, pendingRequest.Method);
                }
                else
                {
                    pendingRequest.CompletionSource.TrySetResult(default);
                    _logger.LogDebug("Resolved request id={Id} method={Method} with empty result", id, pendingRequest.Method);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing LSP message");
        }
    }

    private void HandleNotification(string? method, JsonElement root)
    {
        if (method == "textDocument/publishDiagnostics" && root.TryGetProperty("params", out var @params))
        {
            var diagnostics = @params.Deserialize<PublishDiagnosticsParams>(JsonOptions);
            if (diagnostics != null)
            {
                _diagnosticsCache[diagnostics.Uri] = diagnostics;
                DiagnosticsReceived?.Invoke(diagnostics);
                _logger.LogDebug("Received {Count} diagnostics for {Uri}",
                    diagnostics.Diagnostics.Length, diagnostics.Uri);
            }

            return;
        }

        if (method == "window/logMessage" &&
            root.TryGetProperty("params", out var logParams) &&
            logParams.TryGetProperty("message", out var messageElement))
        {
            var message = messageElement.GetString();
            if (!string.IsNullOrWhiteSpace(message) &&
                message.Contains("Finished loading solution", StringComparison.OrdinalIgnoreCase))
            {
                _workspaceReadySource?.TrySetResult(true);
            }

            return;
        }

        _logger.LogDebug("Ignoring server notification method={Method}", method);
    }

    private async Task HandleServerRequestAsync(JsonElement requestId, string? method, JsonElement? @params, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            _logger.LogWarning("Received server request with no method. Id={Id}", requestId.GetRawText());
            return;
        }

        _logger.LogDebug("Received server request id={Id} method={Method}", requestId.GetRawText(), method);

        try
        {
            var result = CreateServerRequestResult(method, @params);
            await SendMessageAsync(
                new JsonRpcSuccessResponse
                {
                    Id = requestId,
                    Result = result
                },
                cancellationToken);
            _logger.LogDebug("Responded to server request id={Id} method={Method}", requestId.GetRawText(), method);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle server request id={Id} method={Method}", requestId.GetRawText(), method);
            await SendMessageAsync(
                new JsonRpcErrorResponse
                {
                    Id = requestId,
                    Error = new JsonRpcError
                    {
                        Code = -32603,
                        Message = ex.Message
                    }
                },
                cancellationToken);
        }
    }

    private object? CreateServerRequestResult(string method, JsonElement? @params)
    {
        switch (method)
        {
            case "client/registerCapability":
            case "client/unregisterCapability":
            case "window/workDoneProgress/create":
                return null;

            case "workspace/configuration":
                return CreateWorkspaceConfigurationResult(@params);

            case "workspace/workspaceFolders":
                if (string.IsNullOrWhiteSpace(_workspacePath))
                    return null;

                return new[]
                {
                    new WorkspaceFolder
                    {
                        Uri = new Uri(_workspacePath).ToString(),
                        Name = Path.GetFileName(_workspacePath)
                    }
                };

            default:
                _logger.LogWarning("Received unsupported server request method={Method}; responding with null", method);
                return null;
        }
    }

    private static object?[] CreateWorkspaceConfigurationResult(JsonElement? @params)
    {
        if (@params is null ||
            !@params.Value.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<object?>();
        }

        var results = new object?[items.GetArrayLength()];
        for (var i = 0; i < results.Length; i++)
        {
            results[i] = new Dictionary<string, object?>();
        }

        return results;
    }

    private static bool TryGetRequestId(JsonElement idElement, out int id)
    {
        if (idElement.ValueKind == JsonValueKind.Number)
            return idElement.TryGetInt32(out id);

        if (idElement.ValueKind == JsonValueKind.String &&
            int.TryParse(idElement.GetString(), out id))
        {
            return true;
        }

        id = default;
        return false;
    }

    private static Location[]? ParseLocationArrayResult(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Undefined || result.ValueKind == JsonValueKind.Null)
            return null;

        if (result.ValueKind == JsonValueKind.Array)
        {
            if (result.GetArrayLength() == 0)
                return Array.Empty<Location>();

            var locations = new List<Location>(result.GetArrayLength());
            foreach (var item in result.EnumerateArray())
            {
                if (TryParseLocation(item, out var location))
                    locations.Add(location);
            }

            return locations.ToArray();
        }

        return TryParseLocation(result, out var single) ? new[] { single } : null;
    }

    private static bool TryParseLocation(JsonElement element, out Location location)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("targetUri", out _))
        {
            var link = element.Deserialize<LocationLink>(JsonOptions);
            if (link != null)
            {
                location = new Location
                {
                    Uri = link.TargetUri,
                    Range = link.TargetSelectionRange
                };
                return true;
            }
        }

        var parsedLocation = element.Deserialize<Location>(JsonOptions);
        if (parsedLocation != null)
        {
            location = parsedLocation;
            return true;
        }

        location = default!;
        return false;
    }

    private static bool IsProviderSupported(object? provider)
    {
        return provider switch
        {
            null => false,
            bool value => value,
            JsonElement jsonElement => jsonElement.ValueKind switch
            {
                JsonValueKind.False => false,
                JsonValueKind.Null => false,
                JsonValueKind.Undefined => false,
                _ => true
            },
            _ => true
        };
    }

    private static void EnsureServerCapability(string method, object? provider)
    {
        if (IsProviderSupported(provider))
            return;

        throw new NotSupportedException($"The current LSP server does not advertise support for {method}.");
    }

    private static async Task<int?> ReadContentLengthAsync(Stream stream, CancellationToken cancellationToken)
    {
        var headerBytes = new List<byte>();
        var lastFour = new byte[4];
        var lastIndex = 0;

        while (true)
        {
            var buffer = new byte[1];
            var read = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken);
            if (read == 0)
                return null;

            var value = buffer[0];
            headerBytes.Add(value);

            lastFour[lastIndex % 4] = value;
            lastIndex++;

            if (lastIndex >= 4 &&
                lastFour[(lastIndex - 4) % 4] == '\r' &&
                lastFour[(lastIndex - 3) % 4] == '\n' &&
                lastFour[(lastIndex - 2) % 4] == '\r' &&
                lastFour[(lastIndex - 1) % 4] == '\n')
            {
                break;
            }
        }

        var headerText = Encoding.ASCII.GetString(headerBytes.ToArray()).TrimEnd('\r', '\n');
        var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(line.Substring(15).Trim(), out var length))
                    return length;
            }
        }

        return 0;
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int length, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, length - totalRead), cancellationToken);
            if (read == 0)
                return false;
            totalRead += read;
        }

        return true;
    }

    private async Task CleanupFailedStartAsync()
    {
        _readLoopCts?.Cancel();

        if (_readLoopTask != null)
        {
            try
            {
                await _readLoopTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch { }
        }

        if (_lspProcess != null && !_lspProcess.HasExited)
        {
            try
            {
                _lspProcess.Kill();
            }
            catch { }
        }

        _lspProcess?.Dispose();
        _readLoopCts?.Dispose();

        _outputStream = null;
        _readLoopCts = null;
        _readLoopTask = null;
        _lspProcess = null;
        _serverCapabilities = null;
        _workspaceReadySource = null;
        _workspacePath = null;
    }

    public async ValueTask DisposeAsync()
    {
        var readLoopCts = _readLoopCts;
        var readLoopTask = _readLoopTask;

        if (_lspProcess != null && !_lspProcess.HasExited)
        {
            try
            {
                // Send shutdown request
                await SendRequestAsync<object>("shutdown", null, CancellationToken.None);
                await SendNotificationAsync("exit", null, CancellationToken.None);

                if (!_lspProcess.WaitForExit(3000))
                    _lspProcess.Kill();
            }
            catch { }
        }

        readLoopCts?.Cancel();

        if (readLoopTask != null)
        {
            try
            {
                await readLoopTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch { }
        }

        _lspProcess?.Dispose();
        _readLoopCts?.Dispose();
        _initLock.Dispose();
        _writeLock.Dispose();
    }
}
