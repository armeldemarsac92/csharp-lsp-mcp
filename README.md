# C Sharp MCP / C# MCP Server for .NET and XAML

`csharp-lsp-mcp` is a C Sharp MCP server for .NET repositories. It exposes C# and XAML language intelligence, workspace analysis, architecture discovery, DI tracing, test mapping, graph-backed change impact, change planning, and verification planning through the Model Context Protocol.

If you are looking for a `c# mcp` or `c sharp MCP` server that is easy for an LLM agent to parse, this project is built for that use case:

- editor-style C# tools backed by `csharp-ls`
- higher-level codebase analysis tools for .NET solutions
- a persistent Roslyn-backed code graph with incremental refresh
- graph-backed change impact, edit planning, and verification planning
- built-in XAML analysis for WPF and WinUI workflows
- structured JSON output by default for agent consumption

## What This C# MCP Server Does

This MCP server helps AI coding agents and MCP clients inspect medium-to-large .NET codebases faster.

### C# language tools

- workspace setup and shutdown
- file diagnostics
- hover, definition, references, symbols
- completions, code actions, rename
- workspace diagnostics

### Codebase analysis tools

- workspace symbol search
- semantic search for ASP.NET endpoints, hosted services, DI registrations, configuration bindings, and middleware
- implementation lookup
- call hierarchy
- type hierarchy
- project overview
- entrypoint discovery
- DI registration tracing
- one-shot symbol analysis
- production-to-test mapping
- dead code candidate detection

### Graph and change-planning tools

- persistent code graph build and refresh
- graph stats and snapshot inspection
- downstream change impact analysis
- ordered change planning
- verification planning with builds, tests, and focused diagnostics

### XAML tools

- XAML validation
- binding extraction
- resource analysis
- named element inspection
- tree structure extraction
- binding issue detection
- ViewModel interface extraction

## Why This MCP Surface Works Well for LLM Agents

Older C# MCP servers often stop at raw LSP primitives. This project also exposes higher-level, codebase-shaped operations that reduce repeated tool calls:

- `csharp_project_overview`
- `csharp_analyze_symbol`
- `csharp_find_entrypoints`
- `csharp_find_registrations`
- `csharp_semantic_search`
- `csharp_test_map`
- `csharp_find_dead_code_candidates`
- `csharp_build_code_graph`
- `csharp_change_impact`
- `csharp_plan_change`
- `csharp_verify_change`

That makes it easier for an agent to answer questions like:

- "How is this solution structured?"
- "Where are the ASP.NET entrypoints?"
- "Which implementation backs this interface?"
- "Where is this service registered in DI?"
- "What tests probably cover this type?"
- "What breaks if I change this symbol?"
- "Which files should I inspect first?"
- "What build and test commands should I run after the edit?"

## Graph-Backed Planning for LLM Agents

The newer graph features move this project beyond a thin C# LSP wrapper.

You can now build a persistent Roslyn-backed code graph for a solution, reuse it incrementally, and ask higher-level planning questions that are difficult to answer from raw LSP calls alone:

- `csharp_build_code_graph`: build or refresh the persisted graph
- `csharp_graph_stats`: inspect the current snapshot and reuse state
- `csharp_change_impact`: estimate callers, implementations, registrations, entrypoints, and related tests touched by a change
- `csharp_plan_change`: turn impact into an ordered edit and inspection plan
- `csharp_verify_change`: generate build, test, and focused-diagnostics verification steps

This is the part of the MCP surface that gives LLM agents the most leverage during real refactors, because it helps answer:

- what else changes when this type or method changes
- which projects are affected
- which files are the highest-value inspection targets
- which verification commands are worth running first

## Structured Output for Agents

All current C# and XAML tools support `format`. The default is `structured`.

- `structured`: pretty-printed JSON envelope for LLM parsing
- `summary`: short text summary
- `text` and `markdown`: compatibility aliases for `summary`

Structured responses use this shape:

```json
{
  "schemaVersion": 1,
  "tool": "csharp_hover",
  "success": true,
  "summary": "Hover information available.",
  "data": {
    "summary": "Hover information available."
  },
  "error": null
}
```

Error responses keep the same top-level envelope:

```json
{
  "schemaVersion": 1,
  "tool": "csharp_hover",
  "success": false,
  "summary": "Workspace not initialized.",
  "data": null,
  "error": {
    "code": "tool_execution_failed",
    "message": "Workspace not initialized."
  }
}
```

## Repository Layout

This repository is flat:

- `CSharpLspMcp.sln`: solution entrypoint
- `src/CSharpLspMcp/`: MCP server source
- `src/CSharpLspMcp.Tests/`: unit and integration-style tests
- repo root: docs, changelog, contribution guide, GitHub metadata

## Requirements

### 1. .NET 8 SDK

Install the .NET 8 SDK from `https://dotnet.microsoft.com/download/dotnet/8.0`.

### 2. `csharp-ls`

The C# tools depend on `csharp-ls` being available on `PATH`.

```bash
dotnet tool install --global csharp-ls
csharp-ls --version
```

The XAML tools do not depend on `csharp-ls`, but the C# toolchain does.

## Installation

### Build from source

```bash
git clone https://github.com/armeldemarsac92/csharp-lsp-mcp.git
cd csharp-lsp-mcp
dotnet build CSharpLspMcp.sln -c Release
```

Build outputs from the repo root:

- Windows: `src/CSharpLspMcp/bin/Release/net8.0/csharp-lsp-mcp.exe`
- Linux/macOS: `src/CSharpLspMcp/bin/Release/net8.0/csharp-lsp-mcp`
- Cross-platform `dotnet` host: `src/CSharpLspMcp/bin/Release/net8.0/csharp-lsp-mcp.dll`

### Run directly with `dotnet`

From the repo root, for a local development run:

```bash
dotnet run --project src/CSharpLspMcp -- --verbose
```

Or run the built server directly:

```bash
dotnet src/CSharpLspMcp/bin/Release/net8.0/csharp-lsp-mcp.dll --verbose
```

### MCP client configuration

Example MCP configuration:

```json
{
  "mcpServers": {
    "csharp": {
      "command": "dotnet",
      "args": [
        "/absolute/path/to/csharp-lsp-mcp/src/CSharpLspMcp/bin/Release/net8.0/csharp-lsp-mcp.dll"
      ]
    }
  }
}
```

For iterative local development, using `dotnet run` is also valid:

```json
{
  "mcpServers": {
    "csharp": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/absolute/path/to/csharp-lsp-mcp/src/CSharpLspMcp",
        "--",
        "--verbose"
      ]
    }
  }
}
```

If you publish or install the binary separately, point the MCP client to `csharp-lsp-mcp` directly.

## Common Parameter Conventions

These conventions apply across the C# MCP surface:

- `filePath`: usually an absolute path to a `.cs` file; some higher-level tools also accept a workspace-relative path
- `line`: 0-based line number
- `character`: 0-based character position
- `content`: optional unsaved file content; if omitted, tools read from disk; some analyzers also treat empty content as a disk fallback
- `format`: `structured` by default, `summary` for compact human-readable output
- `maxResults`, `maxDocuments`, `maxDiagnosticsPerDocument`: hard caps for agent context control
- `minimumSeverity`: for workspace diagnostics, supports `ALL`, `ERROR`, `WARNING`, `INFO`, and `HINT`
- `includeGenerated`, `includeTests`, `excludePaths`: filtering controls for higher-level analysis tools such as workspace diagnostics and semantic searches
- `excludeDiagnosticCodes`, `excludeDiagnosticSources`: suppress low-value warnings during workspace or change verification runs
- `mode`: graph build mode, `incremental` by default or `full` when a complete rebuild is needed
- `rebuildIfMissing`: graph-backed tools can rebuild persisted graph data automatically when needed
- `includeRegistrations`, `includeEntrypoints`: opt into DI and startup-surface overlays during change-impact analysis

Important workflow rule:

- call `csharp_set_workspace` before using the C# tools
- for the graph-backed planning tools, call `csharp_build_code_graph` once up front on larger solutions so later impact and verification calls can reuse persisted data

## Quick Start

1. Initialize the workspace:

```json
{
  "path": "/path/to/solution-root"
}
```

Call with `csharp_set_workspace`.

2. Inspect the solution:

```json
{
  "maxProjects": 25,
  "format": "structured"
}
```

Call with `csharp_project_overview`.

3. Build the persistent graph:

```json
{
  "mode": "incremental",
  "includeTests": true,
  "format": "structured"
}
```

Call with `csharp_build_code_graph`.

4. Analyze a symbol:

```json
{
  "symbolQuery": "MyCompany.Feature.ServiceBusListener",
  "maxResults": 10,
  "format": "structured"
}
```

Call with `csharp_analyze_symbol`.

5. Plan and verify a change:

```json
{
  "symbolQuery": "MyCompany.Feature.ServiceBusListener",
  "includeTests": true,
  "format": "structured"
}
```

Call with `csharp_change_impact`, `csharp_plan_change`, and `csharp_verify_change`.

## Full Tool Reference

## Workspace Tools

### `csharp_set_workspace`

Sets the current solution or project directory and starts the C# language server.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `path` | `string` | Yes | Path to the solution or project directory. |
| `format` | `string` | No | Output format. Default: `structured`. |

### `csharp_stop`

Stops the C# language server and releases file locks.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `format` | `string` | No | Output format. Default: `structured`. |

### `csharp_workspace_diagnostics`

Returns pull diagnostics across the current workspace.

This tool supports filtering so LLM agents can suppress low-signal diagnostics from generated files, tests, or noisy path segments in larger solutions.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `maxDocuments` | `int` | No | Maximum number of documents to include. Default: `20`. |
| `maxDiagnosticsPerDocument` | `int` | No | Maximum diagnostics to include per document. Default: `10`. |
| `minimumSeverity` | `string` | No | Minimum severity to include: `ALL`, `ERROR`, `WARNING`, `INFO`, or `HINT`. Default: `WARNING`. |
| `includeGenerated` | `bool` | No | Include generated files such as `obj`, `bin`, and `*.g.cs`. Default: `false`. |
| `includeTests` | `bool` | No | Include test files and test projects. Default: `true`. |
| `excludePaths` | `string[]?` | No | Optional file-path substrings to exclude from results. |
| `excludeDiagnosticCodes` | `string[]?` | No | Optional diagnostic codes to exclude, such as `CS8933`, `CS8019`, or `IDE0005`. |
| `excludeDiagnosticSources` | `string[]?` | No | Optional diagnostic sources to exclude, such as `lsp` or `csharp`. |
| `format` | `string` | No | Output format. Default: `structured`. |

## Document Tools

### `csharp_diagnostics`

Returns compiler diagnostics for one C# document.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `filePath` | `string` | Yes | Absolute path to the C# file. |
| `content` | `string?` | No | Optional file content. Reads from disk when omitted. |
| `format` | `string` | No | Output format. Default: `structured`. |

### `csharp_hover`

Returns type information and documentation at a position.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `filePath` | `string` | Yes | Absolute path to the C# file. |
| `line` | `int` | Yes | 0-based line number. |
| `character` | `int` | Yes | 0-based character position. |
| `content` | `string?` | No | Optional file content. |
| `format` | `string` | No | Output format. Default: `structured`. |

### `csharp_completions`

Returns IntelliSense completions at a position.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `filePath` | `string` | Yes | Absolute path to the C# file. |
| `line` | `int` | Yes | 0-based line number. |
| `character` | `int` | Yes | 0-based character position. |
| `content` | `string?` | No | Optional file content. |
| `maxResults` | `int` | No | Maximum completion items. Default: `20`. |
| `format` | `string` | No | Output format. Default: `structured`. |

### `csharp_definition`

Finds the definition of the symbol at a position.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `filePath` | `string` | Yes | Absolute path to the C# file. |
| `line` | `int` | Yes | 0-based line number. |
| `character` | `int` | Yes | 0-based character position. |
| `content` | `string?` | No | Optional file content. |
| `format` | `string` | No | Output format. Default: `structured`. |

### `csharp_references`

Finds references to the symbol at a position.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `filePath` | `string` | Yes | Absolute path to the C# file. |
| `line` | `int` | Yes | 0-based line number. |
| `character` | `int` | Yes | 0-based character position. |
| `content` | `string?` | No | Optional file content. |
| `includeDeclaration` | `bool` | No | Include the declaration in results. Default: `true`. |
| `format` | `string` | No | Output format. Default: `structured`. |

### `csharp_symbols`

Lists document symbols for a single C# file.

For top-level `Program.cs` files, this tool can enrich sparse LSP symbol output with heuristic startup-call symbols such as `Add...`, `Use...`, and `Map...` when the underlying server only returns a thin file/program symbol set.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `filePath` | `string` | Yes | Absolute path to the C# file. |
| `content` | `string?` | No | Optional file content. |
| `format` | `string` | No | Output format. Default: `structured`. |

### `csharp_code_actions`

Returns available quick fixes and refactorings for a selected range.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `filePath` | `string` | Yes | Absolute path to the C# file. |
| `startLine` | `int` | Yes | 0-based start line. |
| `startCharacter` | `int` | Yes | 0-based start character. |
| `endLine` | `int` | Yes | 0-based end line. |
| `endCharacter` | `int` | Yes | 0-based end character. |
| `content` | `string?` | No | Optional file content. |
| `format` | `string` | No | Output format. Default: `structured`. |

### `csharp_rename`

Returns a rename plan for the symbol at a position.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `filePath` | `string` | Yes | Absolute path to the C# file. |
| `line` | `int` | Yes | 0-based line number. |
| `character` | `int` | Yes | 0-based character position. |
| `newName` | `string` | Yes | New symbol name. |
| `content` | `string?` | No | Optional file content. |
| `format` | `string` | No | Output format. Default: `structured`. |

## Search and Hierarchy Tools

### `csharp_search_symbols`

Searches symbols across the current workspace by name.

Fully qualified queries are ranked so production matches beat fallback test matches when the underlying language server only returns simple-name results.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `query` | `string` | Yes | Symbol-name query. Can be empty to inspect top-ranked results. |
| `maxResults` | `int` | No | Maximum number of results. Default: `20`. |
| `format` | `string` | No | Output format. Default: `structured`. |

### `csharp_semantic_search`

Runs named semantic searches across the current workspace.

Supported `query` values:

- `aspnet_endpoints`
- `hosted_services`
- `di_registrations`
- `config_bindings`
- `middleware_pipeline`

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `query` | `string` | Yes | Named search mode. |
| `projectFilter` | `string?` | No | Optional project name or path fragment filter. |
| `includeTests` | `bool` | No | Include results from test code. Default: `false`. |
| `maxResults` | `int` | No | Maximum number of matches. Default: `20`. |
| `format` | `string` | No | Output format. Default: `structured`. |

### `csharp_find_implementations`

Finds implementations of the symbol at a given position.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `filePath` | `string` | Yes | Absolute path to the C# file. |
| `line` | `int` | Yes | 0-based line number. |
| `character` | `int` | Yes | 0-based character position. |
| `content` | `string?` | No | Optional file content. |
| `maxResults` | `int` | No | Maximum implementation results. Default: `20`. |
| `format` | `string` | No | Output format. Default: `structured`. |

### `csharp_call_hierarchy`

Returns incoming and outgoing calls for the symbol at a position.

If `csharp-ls` does not provide outgoing calls for a method, this tool can fall back to definition-based source heuristics and reports that via `usedHeuristicOutgoingFallback` in structured output.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `filePath` | `string` | Yes | Absolute path to the C# file. |
| `line` | `int` | Yes | 0-based line number. |
| `character` | `int` | Yes | 0-based character position. |
| `content` | `string?` | No | Optional file content. |
| `maxResults` | `int` | No | Maximum incoming and outgoing items per side. Default: `20`. |
| `format` | `string` | No | Output format. Default: `structured`. |

### `csharp_type_hierarchy`

Returns immediate supertypes and subtypes for a type.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `filePath` | `string` | Yes | Absolute path to the C# file. |
| `line` | `int` | Yes | 0-based line number. |
| `character` | `int` | Yes | 0-based character position. |
| `content` | `string?` | No | Optional file content. |
| `maxResults` | `int` | No | Maximum supertype and subtype items. Default: `20`. |
| `format` | `string` | No | Output format. Default: `structured`. |

## Architecture and Analysis Tools

### `csharp_project_overview`

Summarizes the current .NET workspace at the solution and project level.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `maxProjects` | `int` | No | Maximum number of projects to include in detail. Default: `25`. |
| `maxPackagesPerProject` | `int` | No | Maximum package references to show per project. Default: `8`. |
| `maxProjectReferencesPerProject` | `int` | No | Maximum project references to show per project. Default: `8`. |
| `format` | `string` | No | Output format. Default: `structured`. |

### `csharp_find_entrypoints`

Finds host projects, startup surfaces, middleware, routes, and hosted services.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `includeAspNetRoutes` | `bool` | No | Include direct ASP.NET route registrations such as `MapGet` and `MapPost`. Default: `true`. |
| `includeHostedServices` | `bool` | No | Include `AddHostedService` registrations and `BackgroundService` implementations. Default: `true`. |
| `includeMiddlewarePipeline` | `bool` | No | Include middleware calls such as `UseAuthentication`. Default: `true`. |
| `maxResults` | `int` | No | Maximum items per section. Default: `20`. |
| `format` | `string` | No | Output format. Default: `structured`. |

### `csharp_find_registrations`

Traces dependency injection registrations and likely consumers.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `query` | `string?` | No | Optional filter by service type, implementation type, or registration text. |
| `includeConsumers` | `bool` | No | Include likely constructor consumers. Default: `true`. |
| `maxResults` | `int` | No | Maximum registrations and consumers per section. Default: `20`. |
| `format` | `string` | No | Output format. Default: `structured`. |

### `csharp_analyze_symbol`

Builds a one-shot symbol report from multiple lower-level analyzers.

Use either `symbolQuery`, or `filePath` with `line` and `character`.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `symbolQuery` | `string?` | No | Workspace symbol query or fully qualified name. |
| `filePath` | `string?` | No | Absolute or workspace-relative file path. Required for position-based analysis. |
| `line` | `int` | No | 0-based line number. Use with `filePath` and `character`. Default: `-1`. |
| `character` | `int` | No | 0-based character position. Use with `filePath` and `line`. Default: `-1`. |
| `content` | `string?` | No | Optional file content. Reads from disk when null or empty. |
| `maxResults` | `int` | No | Maximum references and hierarchy edges to include. Default: `10`. |
| `format` | `string` | No | Output format. Default: `structured`. |

### `csharp_test_map`

Maps production code to likely related tests.

Provide `filePath`, `symbolQuery`, or both.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `filePath` | `string?` | No | Absolute or workspace-relative path to a production C# file. |
| `symbolQuery` | `string?` | No | Symbol name or fully qualified member/type name. |
| `maxResults` | `int` | No | Maximum related tests to return. Default: `10`. |
| `format` | `string` | No | Output format. Default: `structured`. |

### `csharp_find_dead_code_candidates`

Finds best-effort dead code candidates in the current workspace.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `includePrivateMembers` | `bool` | No | Include unused private methods and fields. Default: `true`. |
| `includeInternalTypes` | `bool` | No | Include unreferenced internal types. Default: `true`. |
| `includeTests` | `bool` | No | Include candidates from test projects and test paths. Default: `false`. |
| `maxResults` | `int` | No | Maximum candidates to return. Default: `20`. |
| `format` | `string` | No | Output format. Default: `structured`. |

## Graph, Planning, and Verification Tools

### `csharp_build_code_graph`

Builds or refreshes the persistent Roslyn-backed workspace graph.

Incremental mode reuses unchanged project graph slices from the last compatible snapshot, which makes repeated impact and verification calls much cheaper on medium-to-large solutions.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `path` | `string?` | No | Optional workspace, solution, or project path. Uses the current workspace when omitted. |
| `mode` | `string` | No | Build mode: `incremental` (default) or `full`. |
| `includeTests` | `bool` | No | Include test projects and test paths in the graph. Default: `true`. |
| `includeGenerated` | `bool` | No | Include generated files such as `obj`, `bin`, and `*.g.cs`. Default: `false`. |
| `format` | `string` | No | Output format. Default: `structured`. |

### `csharp_graph_stats`

Reads the persisted graph summary for the current workspace or an explicit path.

Useful fields in structured output include snapshot identity, project and symbol counts, edge counts, reuse statistics, and warnings emitted during the last build.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `path` | `string?` | No | Optional workspace, solution, or project path. Uses the current workspace when omitted. |
| `format` | `string` | No | Output format. Default: `structured`. |

### `csharp_change_impact`

Estimates downstream impact for changing a symbol or file using the persisted graph plus higher-level architecture and test overlays.

This tool is designed to answer "what breaks if I change this?" without forcing the agent to manually combine references, implementations, DI registrations, entrypoints, and test hints across multiple calls.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `symbolQuery` | `string?` | No | Fully qualified symbol name, documentation ID, or simple symbol query. |
| `filePath` | `string?` | No | Optional file path to analyze instead of or in addition to a symbol query. |
| `includeTests` | `bool` | No | Include related tests in the impact report. Default: `true`. |
| `includeRegistrations` | `bool` | No | Include matching DI registrations and consumers. Default: `true`. |
| `includeEntrypoints` | `bool` | No | Include routes, startup surfaces, hosted services, and other entrypoints. Default: `true`. |
| `rebuildIfMissing` | `bool` | No | Rebuild the graph automatically when missing or stale. Default: `true`. |
| `maxResults` | `int` | No | Maximum items to return per section. Default: `20`. |
| `format` | `string` | No | Output format. Default: `structured`. |

### `csharp_plan_change`

Builds an ordered change plan for a symbol or file: primary edit targets, affected projects, inspection order, and verification steps.

This is the high-level planning companion to `csharp_change_impact`.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `request` | `string?` | No | Optional short description of the intended change. |
| `symbolQuery` | `string?` | No | Fully qualified symbol name, documentation ID, or simple symbol query. |
| `filePath` | `string?` | No | Optional file path to analyze instead of or in addition to a symbol query. |
| `includeTests` | `bool` | No | Include related tests and test-driven verification steps. Default: `true`. |
| `rebuildIfMissing` | `bool` | No | Rebuild the graph automatically when missing or stale. Default: `true`. |
| `maxResults` | `int` | No | Maximum items to include per section. Default: `10`. |
| `format` | `string` | No | Output format. Default: `structured`. |

### `csharp_verify_change`

Prepares a verification plan for changed symbols or files: build commands, test commands, focused diagnostics, and ordered verification steps.

Use this after an edit, or use it up front to decide what the smallest safe validation loop looks like before touching code.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `request` | `string?` | No | Optional short description of the intended change. |
| `symbolQuery` | `string?` | No | Fully qualified symbol name, documentation ID, or simple symbol query. |
| `filePath` | `string?` | No | Optional file path to analyze instead of or in addition to a symbol query. |
| `changedFiles` | `string[]?` | No | Optional changed files to prioritize in diagnostics and project selection. |
| `includeTests` | `bool` | No | Include test projects and test diagnostics. Default: `true`. |
| `rebuildIfMissing` | `bool` | No | Rebuild the graph automatically when missing or stale. Default: `true`. |
| `minimumSeverity` | `string` | No | Minimum severity for focused diagnostics: `ALL`, `ERROR`, `WARNING` (default), `INFO`, or `HINT`. |
| `excludeDiagnosticCodes` | `string[]?` | No | Optional diagnostic codes to exclude, such as `IDE0005` or `CS8933`. |
| `excludeDiagnosticSources` | `string[]?` | No | Optional diagnostic sources to exclude, such as `csharp` or `Style`. |
| `maxResults` | `int` | No | Maximum items to include per section. Default: `10`. |
| `format` | `string` | No | Output format. Default: `structured`. |

## XAML Tools

### `xaml_validate`

Validates a XAML file and returns parse and semantic diagnostics.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `filePath` | `string` | Yes | Path to the XAML file. |
| `content` | `string?` | No | Optional XAML content. Reads from disk when omitted. |
| `projectPath` | `string?` | No | Optional project path for assembly-aware validation. |
| `format` | `string` | No | Output format. Default: `structured`. |

### `xaml_bindings`

Extracts data bindings from a XAML file.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `filePath` | `string` | Yes | Path to the XAML file. |
| `content` | `string?` | No | Optional XAML content. |
| `format` | `string` | No | Output format. Default: `structured`. |

### `xaml_resources`

Lists resource definitions and references in a XAML file.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `filePath` | `string` | Yes | Path to the XAML file. |
| `content` | `string?` | No | Optional XAML content. |
| `format` | `string` | No | Output format. Default: `structured`. |

### `xaml_names`

Lists all named elements and duplicate `x:Name` values.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `filePath` | `string` | Yes | Path to the XAML file. |
| `content` | `string?` | No | Optional XAML content. |
| `format` | `string` | No | Output format. Default: `structured`. |

### `xaml_structure`

Builds a simplified XAML element tree.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `filePath` | `string` | Yes | Path to the XAML file. |
| `content` | `string?` | No | Optional XAML content. |
| `maxDepth` | `int` | No | Maximum tree depth to include. Default: `10`. |
| `format` | `string` | No | Output format. Default: `structured`. |

### `xaml_find_binding_errors`

Finds likely binding issues in a XAML file.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `filePath` | `string` | Yes | Path to the XAML file. |
| `content` | `string?` | No | Optional XAML content. |
| `format` | `string` | No | Output format. Default: `structured`. |

### `xaml_extract_viewmodel`

Generates a ViewModel interface from inferred binding properties.

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `filePath` | `string` | Yes | Path to the XAML file. |
| `content` | `string?` | No | Optional XAML content. |
| `interfaceName` | `string` | No | Generated interface name. Default: `IViewModel`. |
| `format` | `string` | No | Output format. Default: `structured`. |

## Example C# MCP Workflows

### Understand a new .NET solution

1. `csharp_set_workspace`
2. `csharp_project_overview`
3. `csharp_find_entrypoints`
4. `csharp_find_registrations`

### Analyze one service or controller

1. `csharp_search_symbols`
2. `csharp_analyze_symbol`
3. `csharp_call_hierarchy`
4. `csharp_find_implementations`
5. `csharp_test_map`

### Audit a codebase for cleanup

1. `csharp_workspace_diagnostics`
2. `csharp_find_dead_code_candidates`
3. `csharp_semantic_search`

### Plan and verify a risky refactor

1. `csharp_build_code_graph`
2. `csharp_change_impact`
3. `csharp_plan_change`
4. `csharp_verify_change`

## Notes and Limitations

- The C# analysis path depends on `csharp-ls`, so its capabilities and quirks affect the low-level editor-style tools.
- The higher-level planning path now combines LSP, Roslyn, and a persisted graph. That gives agents much stronger refactor support, but some overlays still remain heuristic.
- Higher-level tools such as semantic search, test mapping, and dead-code detection are intentionally heuristic. They are designed to be useful for agents, not to act as a full formal static-analysis platform.
- Graph-backed tools are most effective after `csharp_build_code_graph` has been run once for the current workspace. Incremental refresh is supported, but a `full` rebuild is still useful after major repository churn.
- DI registrations and entrypoints are now persisted in the graph for impact analysis, but the graph is still a pragmatic agent-oriented model rather than a full general-purpose code-intelligence database.
- `csharp_set_workspace` should be called before other C# tools so the language server can load the solution correctly.

## Development

```bash
dotnet build
dotnet test
```

Verbose server logging from the repo root:

```bash
dotnet run --project src/CSharpLspMcp -- --verbose
```

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## Changelog

See [CHANGELOG.md](CHANGELOG.md).

## License

MIT. See [LICENSE](LICENSE).
