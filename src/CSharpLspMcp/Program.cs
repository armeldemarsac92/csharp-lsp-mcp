using CSharpLspMcp.Lsp;
using CSharpLspMcp.Analysis.Architecture;
using CSharpLspMcp.Analysis.Graph;
using CSharpLspMcp.Analysis.Lsp;
using CSharpLspMcp.Analysis.Planning;
using CSharpLspMcp.Analysis.Quality;
using CSharpLspMcp.Analysis.Testing;
using CSharpLspMcp.Analysis.Verification;
using CSharpLspMcp.Storage.Graph;
using CSharpLspMcp.Tools;
using CSharpLspMcp.Tools.Analysis;
using CSharpLspMcp.Tools.Architecture;
using CSharpLspMcp.Tools.Document;
using CSharpLspMcp.Tools.Graph;
using CSharpLspMcp.Tools.Hierarchy;
using CSharpLspMcp.Tools.Planning;
using CSharpLspMcp.Tools.Search;
using CSharpLspMcp.Tools.Verification;
using CSharpLspMcp.Tools.Workspace;
using CSharpLspMcp.Workspace;
using CSharpLspMcp.Workspace.Roslyn;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CSharpLspMcp;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Handle version flag
        if (args.Contains("--version") || args.Contains("-v"))
        {
            Console.WriteLine("csharp-lsp-mcp version 1.0.0");
            return 0;
        }

        // Handle help flag
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintHelp();
            return 0;
        }

        try
        {
            var builder = Host.CreateApplicationBuilder(args);

            // Configure logging to stderr so it doesn't interfere with MCP protocol
            builder.Logging.AddConsole(options =>
            {
                options.LogToStandardErrorThreshold = LogLevel.Trace;
            });

            // Set log level based on environment or args
            var logLevel = LogLevel.Warning;
            if (args.Contains("--verbose") || args.Contains("-V"))
                logLevel = LogLevel.Debug;
            if (Environment.GetEnvironmentVariable("MCP_DEBUG") == "1")
                logLevel = LogLevel.Trace;

            builder.Logging.SetMinimumLevel(logLevel);

            // Register solution filter and LSP client as singletons
            builder.Services.AddSingleton<WorkspaceState>();
            builder.Services.AddSingleton<SolutionFilter>();
            builder.Services.AddSingleton<LspClient>();
            builder.Services.AddSingleton<CSharpWorkspaceSession>();
            builder.Services.AddSingleton<CSharpDocumentAnalysisService>();
            builder.Services.AddSingleton<CSharpSearchAnalysisService>();
            builder.Services.AddSingleton<CSharpHierarchyAnalysisService>();
            builder.Services.AddSingleton<CSharpWorkspaceAnalysisService>();
            builder.Services.AddSingleton<IWorkspaceDiagnosticsProvider>(serviceProvider => serviceProvider.GetRequiredService<CSharpWorkspaceAnalysisService>());
            builder.Services.AddSingleton<CSharpRoslynWorkspaceHost>();
            builder.Services.AddSingleton<CSharpGraphCacheStore>();
            builder.Services.AddSingleton<CSharpGraphBuildService>();
            builder.Services.AddSingleton<CSharpChangeImpactService>();
            builder.Services.AddSingleton<CSharpChangePlanService>();
            builder.Services.AddSingleton<CSharpChangeVerificationService>();
            builder.Services.AddSingleton<CSharpProjectOverviewAnalysisService>();
            builder.Services.AddSingleton<CSharpEntrypointAnalysisService>();
            builder.Services.AddSingleton<CSharpRegistrationAnalysisService>();
            builder.Services.AddSingleton<CSharpSemanticSearchAnalysisService>();
            builder.Services.AddSingleton<CSharpSymbolAnalysisService>();
            builder.Services.AddSingleton<CSharpTestMapAnalysisService>();
            builder.Services.AddSingleton<CSharpDeadCodeAnalysisService>();

            // Configure MCP server with official SDK
            builder.Services
                .AddMcpServer(options =>
                {
                    options.ServerInfo = new Implementation
                    {
                        Name = "csharp-lsp-mcp",
                        Version = "1.0.0"
                    };
                })
                .WithStdioServerTransport()
                .WithTools<WorkspaceTools>()
                .WithTools<DocumentTools>()
                .WithTools<SearchTools>()
                .WithTools<GraphTools>()
                .WithTools<PlanningTools>()
                .WithTools<VerificationTools>()
                .WithTools<HierarchyTools>()
                .WithTools<ArchitectureTools>()
                .WithTools<AnalysisTools>()
                .WithTools<XamlTools>();

            var app = builder.Build();

            await app.RunAsync();

            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Server crashed: {ex.Message}");
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine(@"csharp-lsp-mcp - MCP Server for C# and XAML Language Intelligence

USAGE:
    csharp-lsp-mcp [OPTIONS]

OPTIONS:
    -h, --help      Show this help message
    -v, --version   Show version information
    -V, --verbose   Enable verbose logging (to stderr)

ENVIRONMENT VARIABLES:
    MCP_DEBUG=1     Enable trace-level logging

DESCRIPTION:
    This MCP server provides C# language intelligence via csharp-ls and
    built-in XAML analysis for WPF/WinUI projects.

    Built with the official Microsoft Model Context Protocol SDK.

PREREQUISITES:
    Install csharp-ls: dotnet tool install --global csharp-ls

USAGE WITH CLAUDE:
    Add to your Claude configuration:
    {
      ""mcpServers"": {
        ""csharp"": {
          ""command"": ""csharp-lsp-mcp""
        }
      }
    }

AVAILABLE TOOLS:
  C# Tools (require csharp-ls):
    csharp_set_workspace  - Set the workspace/solution directory
    csharp_diagnostics    - Get compiler errors and warnings
    csharp_hover          - Get type info at a position
    csharp_completions    - Get IntelliSense completions
    csharp_definition     - Go to definition
    csharp_references     - Find all references
    csharp_symbols        - Get document symbols
    csharp_search_symbols - Search workspace symbols
    csharp_semantic_search - Run named semantic searches across the workspace
    csharp_build_code_graph - Build or refresh a persistent Roslyn-backed code graph
    csharp_graph_stats - Read persisted code graph stats for the workspace
    csharp_change_impact - Analyze likely downstream impact of changing a symbol or file
    csharp_plan_change - Build an ordered edit and verification plan for a requested change
    csharp_verify_change - Prepare build, test, and diagnostics verification steps for a change
    csharp_find_implementations - Find implementations of a symbol
    csharp_call_hierarchy - Get incoming and outgoing calls
    csharp_type_hierarchy - Get supertypes and subtypes for a type
    csharp_project_overview - Summarize projects, dependencies, and entrypoints
    csharp_find_entrypoints - Discover hosts, routes, middleware, and hosted services
    csharp_find_registrations - Trace DI registrations and likely consumers
    csharp_analyze_symbol - Build a one-shot symbol analysis report
    csharp_test_map - Map production files or symbols to likely related tests
    csharp_find_dead_code_candidates - Find best-effort dead code candidates
    csharp_code_actions   - Get available code actions
    csharp_rename         - Preview symbol rename
    csharp_workspace_diagnostics - Get pull diagnostics across the workspace

  XAML Tools (built-in):
    xaml_validate         - Validate XAML for errors and issues
    xaml_bindings         - Extract and analyze data bindings
    xaml_resources        - List and check resource references
    xaml_names            - List x:Name declarations
    xaml_structure        - Show element tree structure
    xaml_find_binding_errors - Find binding errors
    xaml_extract_viewmodel   - Generate ViewModel from bindings
");
    }
}
