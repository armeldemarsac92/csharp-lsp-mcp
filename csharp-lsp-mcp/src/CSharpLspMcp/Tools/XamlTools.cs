using System.ComponentModel;
using CSharpLspMcp.Contracts.Common;
using CSharpLspMcp.Workspace;
using CSharpLspMcp.Xaml;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CSharpLspMcp.Tools;

[McpServerToolType]
public sealed class XamlTools : CSharpToolBase
{
    private readonly ILogger<XamlTools> _logger;
    private readonly XamlAnalyzer _analyzer;
    private readonly XamlParser _parser;
    private readonly WorkspaceState _workspaceState;

    public XamlTools(ILogger<XamlTools> logger, WorkspaceState workspaceState)
    {
        _logger = logger;
        _workspaceState = workspaceState;
        _analyzer = new XamlAnalyzer(LoggerFactory.Create(b => b.AddConsole()).CreateLogger<XamlAnalyzer>());
        _parser = new XamlParser();
    }

    [McpServerTool(Name = "xaml_validate")]
    [Description("Validate a XAML file for errors, warnings, and common issues. Checks type references, property names, resource keys, binding paths, and more.")]
    public Task<string> ValidateAsync(
        [Description("Path to the XAML file")] string filePath,
        [Description("XAML content (optional, reads from file if not provided)")] string? content = null,
        [Description("Path to project directory for assembly-based validation (optional)")] string? projectPath = null,
        [Description("Output format: structured (default) or summary.")] string format = "structured",
        CancellationToken cancellationToken = default)
        => ExecuteStructuredToolAsync(
            _logger,
            "xaml_validate",
            format,
            async ct =>
            {
                content ??= await File.ReadAllTextAsync(filePath, ct);
                projectPath ??= _workspaceState.CurrentPath;

                var result = await _analyzer.AnalyzeAsync(filePath, content, projectPath, ct);
                var parseResult = result.ParseResult;
                var errors = result.Diagnostics.Count(d => d.Severity == XamlDiagnosticSeverity.Error);
                var warnings = result.Diagnostics.Count(d => d.Severity == XamlDiagnosticSeverity.Warning);

                return new XamlValidationResponse(
                    Summary: result.Diagnostics.Count == 0
                        ? $"No issues found in {Path.GetFileName(filePath)}."
                        : $"Found {result.Diagnostics.Count} issue(s) in {Path.GetFileName(filePath)}.",
                    FilePath: filePath,
                    ClassName: parseResult?.ClassName,
                    ClassNamespace: parseResult?.ClassNamespace,
                    NamedElementCount: parseResult?.NamedElements.Count ?? 0,
                    ResourceCount: parseResult?.Resources.Count ?? 0,
                    BindingCount: parseResult?.Bindings.Count ?? 0,
                    ErrorCount: errors,
                    WarningCount: warnings,
                    Diagnostics: result.Diagnostics
                        .Select(MapDiagnostic)
                        .ToArray());
            },
            cancellationToken);

    [McpServerTool(Name = "xaml_bindings")]
    [Description("Extract and analyze all data bindings in a XAML file. Shows binding paths, modes, converters, and potential issues.")]
    public Task<string> GetBindingsAsync(
        [Description("Path to the XAML file")] string filePath,
        [Description("XAML content (optional)")] string? content = null,
        [Description("Output format: structured (default) or summary.")] string format = "structured",
        CancellationToken cancellationToken = default)
        => ExecuteStructuredToolAsync(
            _logger,
            "xaml_bindings",
            format,
            async ct =>
            {
                content ??= await File.ReadAllTextAsync(filePath, ct);
                var parseResult = _parser.Parse(content, filePath);

                var uniquePaths = parseResult.Bindings
                    .Where(binding => !string.IsNullOrEmpty(binding.Path))
                    .Select(binding => binding.Path.Split('.')[0])
                    .Distinct()
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                return new XamlBindingsResponse(
                    Summary: parseResult.Bindings.Count == 0
                        ? $"No data bindings found in {Path.GetFileName(filePath)}."
                        : $"Found {parseResult.Bindings.Count} binding(s) in {Path.GetFileName(filePath)}.",
                    FilePath: filePath,
                    TotalBindings: parseResult.Bindings.Count,
                    Bindings: parseResult.Bindings
                        .OrderBy(binding => binding.Line)
                        .Select(binding => new XamlBindingItem(
                            binding.Path,
                            binding.Line,
                            binding.Column,
                            binding.Mode,
                            binding.Converter,
                            binding.ElementName,
                            binding.Source,
                            binding.RelativeSource))
                        .ToArray(),
                    UniqueRootProperties: uniquePaths);
            },
            cancellationToken);

    [McpServerTool(Name = "xaml_resources")]
    [Description("List all resources defined in a XAML file and check for unused or missing resource references.")]
    public Task<string> GetResourcesAsync(
        [Description("Path to the XAML file")] string filePath,
        [Description("XAML content (optional)")] string? content = null,
        [Description("Output format: structured (default) or summary.")] string format = "structured",
        CancellationToken cancellationToken = default)
        => ExecuteStructuredToolAsync(
            _logger,
            "xaml_resources",
            format,
            async ct =>
            {
                content ??= await File.ReadAllTextAsync(filePath, ct);
                var parseResult = _parser.Parse(content, filePath);

                var staticRefs = parseResult.ResourceReferences.Where(reference => reference.IsStatic).ToArray();
                var dynamicRefs = parseResult.ResourceReferences.Where(reference => !reference.IsStatic).ToArray();
                var usedKeys = parseResult.ResourceReferences.Select(reference => reference.Key).ToHashSet();
                var unusedResources = parseResult.Resources
                    .Where(resource => !usedKeys.Contains(resource.Key) && !resource.IsStyle)
                    .ToArray();

                return new XamlResourcesResponse(
                    Summary: $"Found {parseResult.Resources.Count} resource definition(s) and {parseResult.ResourceReferences.Count} resource reference(s).",
                    FilePath: filePath,
                    Resources: parseResult.Resources
                        .OrderBy(resource => resource.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(resource => new XamlResourceItem(
                            resource.Key,
                            resource.Type,
                            resource.Line,
                            resource.Column,
                            resource.IsStyle,
                            resource.TargetType))
                        .ToArray(),
                    StaticReferences: staticRefs.Select(MapResourceReference).ToArray(),
                    DynamicReferences: dynamicRefs.Select(MapResourceReference).ToArray(),
                    UnusedResources: unusedResources
                        .Select(resource => new XamlResourceItem(
                            resource.Key,
                            resource.Type,
                            resource.Line,
                            resource.Column,
                            resource.IsStyle,
                            resource.TargetType))
                        .ToArray());
            },
            cancellationToken);

    [McpServerTool(Name = "xaml_names")]
    [Description("List all x:Name declarations in a XAML file. Useful for finding named elements and checking for duplicates.")]
    public Task<string> GetNamesAsync(
        [Description("Path to the XAML file")] string filePath,
        [Description("XAML content (optional)")] string? content = null,
        [Description("Output format: structured (default) or summary.")] string format = "structured",
        CancellationToken cancellationToken = default)
        => ExecuteStructuredToolAsync(
            _logger,
            "xaml_names",
            format,
            async ct =>
            {
                content ??= await File.ReadAllTextAsync(filePath, ct);
                var parseResult = _parser.Parse(content, filePath);

                var duplicates = parseResult.NamedElements
                    .GroupBy(element => element.Name)
                    .Where(group => group.Count() > 1)
                    .Select(group => new XamlDuplicateNameItem(
                        group.Key,
                        group.Select(item => item.Line).OrderBy(line => line).ToArray()))
                    .ToArray();

                return new XamlNamesResponse(
                    Summary: parseResult.NamedElements.Count == 0
                        ? $"No named elements found in {Path.GetFileName(filePath)}."
                        : $"Found {parseResult.NamedElements.Count} named element(s) in {Path.GetFileName(filePath)}.",
                    FilePath: filePath,
                    NamedElements: parseResult.NamedElements
                        .OrderBy(element => element.Type, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(element => element.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(element => new XamlNamedElementItem(
                            element.Name,
                            element.Type,
                            element.Line,
                            element.Column))
                        .ToArray(),
                    DuplicateNames: duplicates);
            },
            cancellationToken);

    [McpServerTool(Name = "xaml_structure")]
    [Description("Show the element tree structure of a XAML file. Displays hierarchy of controls and their types.")]
    public Task<string> GetStructureAsync(
        [Description("Path to the XAML file")] string filePath,
        [Description("XAML content (optional)")] string? content = null,
        [Description("Maximum depth to display (default: 10)")] int maxDepth = 10,
        [Description("Output format: structured (default) or summary.")] string format = "structured",
        CancellationToken cancellationToken = default)
        => ExecuteStructuredToolAsync(
            _logger,
            "xaml_structure",
            format,
            async ct =>
            {
                content ??= await File.ReadAllTextAsync(filePath, ct);
                var parseResult = _parser.Parse(content, filePath);

                return new XamlStructureResponse(
                    Summary: parseResult.Root == null
                        ? $"Could not parse XAML structure for {Path.GetFileName(filePath)}."
                        : $"Parsed XAML structure for {Path.GetFileName(filePath)}.",
                    FilePath: filePath,
                    Root: parseResult.Root == null ? null : MapElement(parseResult.Root, 0, Math.Max(1, maxDepth)),
                    ParseErrors: parseResult.ParseErrors.Select(MapDiagnostic).ToArray());
            },
            cancellationToken);

    [McpServerTool(Name = "xaml_find_binding_errors")]
    [Description("Find potential binding errors in XAML. Checks for ElementName references to non-existent names, converter references, and suspicious binding patterns.")]
    public Task<string> FindBindingErrorsAsync(
        [Description("Path to the XAML file")] string filePath,
        [Description("XAML content (optional)")] string? content = null,
        [Description("Output format: structured (default) or summary.")] string format = "structured",
        CancellationToken cancellationToken = default)
        => ExecuteStructuredToolAsync(
            _logger,
            "xaml_find_binding_errors",
            format,
            async ct =>
            {
                content ??= await File.ReadAllTextAsync(filePath, ct);
                var parseResult = _parser.Parse(content, filePath);

                var issues = new List<XamlBindingIssueItem>();
                var namedElements = parseResult.NamedElements.Select(element => element.Name).ToHashSet();
                foreach (var binding in parseResult.Bindings.Where(binding => !string.IsNullOrEmpty(binding.ElementName)))
                {
                    if (!namedElements.Contains(binding.ElementName!))
                    {
                        issues.Add(new XamlBindingIssueItem(
                            "ERROR",
                            binding.Line,
                            binding.Column,
                            $"ElementName='{binding.ElementName}' references non-existent element"));
                    }
                }

                var resourceKeys = parseResult.Resources.Select(resource => resource.Key).ToHashSet();
                foreach (var binding in parseResult.Bindings.Where(binding => !string.IsNullOrEmpty(binding.Converter)))
                {
                    if (!resourceKeys.Contains(binding.Converter!))
                    {
                        issues.Add(new XamlBindingIssueItem(
                            "WARNING",
                            binding.Line,
                            binding.Column,
                            $"Converter='{binding.Converter}' not found in file resources"));
                    }
                }

                foreach (var binding in parseResult.Bindings)
                {
                    if (binding.Path.Contains("..", StringComparison.Ordinal))
                    {
                        issues.Add(new XamlBindingIssueItem(
                            "WARNING",
                            binding.Line,
                            binding.Column,
                            $"Suspicious path with '..': {binding.Path}"));
                    }

                    if (binding.Path.EndsWith(".", StringComparison.Ordinal))
                    {
                        issues.Add(new XamlBindingIssueItem(
                            "ERROR",
                            binding.Line,
                            binding.Column,
                            $"Path ends with '.': {binding.Path}"));
                    }

                    if (binding.Path.StartsWith("[", StringComparison.Ordinal) && !binding.Path.Contains("]", StringComparison.Ordinal))
                    {
                        issues.Add(new XamlBindingIssueItem(
                            "ERROR",
                            binding.Line,
                            binding.Column,
                            $"Unclosed indexer in path: {binding.Path}"));
                    }

                    if (string.IsNullOrEmpty(binding.Path) &&
                        string.IsNullOrEmpty(binding.ElementName) &&
                        !string.IsNullOrEmpty(binding.Mode))
                    {
                        issues.Add(new XamlBindingIssueItem(
                            "WARNING",
                            binding.Line,
                            binding.Column,
                            "Binding with Mode but no Path specified"));
                    }
                }

                return new XamlBindingErrorsResponse(
                    Summary: issues.Count == 0
                        ? $"No binding issues detected in {Path.GetFileName(filePath)}."
                        : $"Found {issues.Count} potential binding issue(s) in {Path.GetFileName(filePath)}.",
                    FilePath: filePath,
                    Issues: issues.OrderBy(issue => issue.Line).ThenBy(issue => issue.Column).ToArray());
            },
            cancellationToken);

    [McpServerTool(Name = "xaml_extract_viewmodel")]
    [Description("Extract the expected ViewModel interface from XAML bindings. Generates a C# interface with properties needed by the XAML bindings.")]
    public Task<string> ExtractViewModelAsync(
        [Description("Path to the XAML file")] string filePath,
        [Description("XAML content (optional)")] string? content = null,
        [Description("Name for generated interface (default: IViewModel)")] string interfaceName = "IViewModel",
        [Description("Output format: structured (default) or summary.")] string format = "structured",
        CancellationToken cancellationToken = default)
        => ExecuteStructuredToolAsync(
            _logger,
            "xaml_extract_viewmodel",
            format,
            async ct =>
            {
                content ??= await File.ReadAllTextAsync(filePath, ct);
                var parseResult = _parser.Parse(content, filePath);

                var properties = parseResult.Bindings
                    .Where(binding => !string.IsNullOrEmpty(binding.Path))
                    .Select(binding => binding.Path.Split('.')[0])
                    .Where(property => !property.StartsWith("[", StringComparison.Ordinal) && !property.StartsWith("(", StringComparison.Ordinal))
                    .Distinct()
                    .OrderBy(property => property, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var propertyTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var binding in parseResult.Bindings)
                {
                    if (string.IsNullOrEmpty(binding.Path))
                        continue;

                    var rootProperty = binding.Path.Split('.')[0];
                    if (binding.Path.Contains("Command", StringComparison.Ordinal) || rootProperty.EndsWith("Command", StringComparison.Ordinal))
                        propertyTypes[rootProperty] = "ICommand";
                    else if (binding.Path.Contains("Items", StringComparison.Ordinal) || rootProperty.EndsWith("Items", StringComparison.Ordinal) ||
                             rootProperty.EndsWith("Collection", StringComparison.Ordinal) || rootProperty.EndsWith("List", StringComparison.Ordinal))
                        propertyTypes[rootProperty] = "IEnumerable";
                    else if (rootProperty.StartsWith("Is", StringComparison.Ordinal) || rootProperty.StartsWith("Has", StringComparison.Ordinal) ||
                             rootProperty.StartsWith("Can", StringComparison.Ordinal) || rootProperty.EndsWith("Enabled", StringComparison.Ordinal) ||
                             rootProperty.EndsWith("Visible", StringComparison.Ordinal))
                        propertyTypes[rootProperty] = "bool";
                    else if (rootProperty.EndsWith("Count", StringComparison.Ordinal) || rootProperty.EndsWith("Index", StringComparison.Ordinal) ||
                             rootProperty.EndsWith("Number", StringComparison.Ordinal))
                        propertyTypes[rootProperty] = "int";
                    else if (rootProperty.EndsWith("Date", StringComparison.Ordinal) || rootProperty.EndsWith("Time", StringComparison.Ordinal))
                        propertyTypes[rootProperty] = "DateTime";
                    else if (!propertyTypes.ContainsKey(rootProperty))
                        propertyTypes[rootProperty] = "object";
                }

                var propertyItems = properties
                    .Select(property => new XamlViewModelPropertyItem(property, propertyTypes.GetValueOrDefault(property, "object")))
                    .ToArray();

                var generatedCode = BuildGeneratedInterface(filePath, interfaceName, parseResult.Bindings.Count, propertyItems);

                return new XamlExtractViewModelResponse(
                    Summary: $"Generated {interfaceName} with {propertyItems.Length} inferred propert{(propertyItems.Length == 1 ? "y" : "ies")}.",
                    FilePath: filePath,
                    InterfaceName: interfaceName,
                    BindingCount: parseResult.Bindings.Count,
                    Properties: propertyItems,
                    GeneratedCode: generatedCode,
                    Notes:
                    [
                        "Types are inferred from naming conventions.",
                        "Review and adjust the generated interface as needed."
                    ]);
            },
            cancellationToken);

    private static XamlDiagnosticItem MapDiagnostic(XamlDiagnostic diagnostic)
        => new(
            diagnostic.Severity.ToString().ToUpperInvariant(),
            diagnostic.Line,
            diagnostic.Column,
            diagnostic.Message,
            diagnostic.Code,
            diagnostic.Source);

    private static XamlResourceReferenceItem MapResourceReference(XamlResourceReference reference)
        => new(reference.Key, reference.IsStatic, reference.Line, reference.Column);

    private static XamlElementNode MapElement(XamlElement element, int depth, int maxDepth)
    {
        if (depth >= maxDepth)
        {
            return new XamlElementNode(
                element.Name,
                element.Line,
                element.Column,
                element.Attributes.FirstOrDefault(attribute => attribute.Name is "x:Name" or "Name")?.Value,
                Array.Empty<XamlElementNode>(),
                true);
        }

        return new XamlElementNode(
            element.Name,
            element.Line,
            element.Column,
            element.Attributes.FirstOrDefault(attribute => attribute.Name is "x:Name" or "Name")?.Value,
            element.Children
                .Where(child => !child.Name.Contains(".", StringComparison.Ordinal))
                .Select(child => MapElement(child, depth + 1, maxDepth))
                .ToArray(),
            false);
    }

    private static string BuildGeneratedInterface(
        string filePath,
        string interfaceName,
        int bindingCount,
        IReadOnlyCollection<XamlViewModelPropertyItem> properties)
    {
        var lines = new List<string>
        {
            $"// Generated ViewModel interface from {Path.GetFileName(filePath)}",
            $"// Bindings found: {bindingCount}",
            string.Empty,
            $"public interface {interfaceName}",
            "{"
        };

        foreach (var property in properties)
            lines.Add($"    {property.Type} {property.Name} {{ get; }}");

        lines.Add("}");
        return string.Join(Environment.NewLine, lines);
    }

    public sealed record XamlValidationResponse(
        string Summary,
        string FilePath,
        string? ClassName,
        string? ClassNamespace,
        int NamedElementCount,
        int ResourceCount,
        int BindingCount,
        int ErrorCount,
        int WarningCount,
        XamlDiagnosticItem[] Diagnostics) : IStructuredToolResult;

    public sealed record XamlBindingsResponse(
        string Summary,
        string FilePath,
        int TotalBindings,
        XamlBindingItem[] Bindings,
        string[] UniqueRootProperties) : IStructuredToolResult;

    public sealed record XamlResourcesResponse(
        string Summary,
        string FilePath,
        XamlResourceItem[] Resources,
        XamlResourceReferenceItem[] StaticReferences,
        XamlResourceReferenceItem[] DynamicReferences,
        XamlResourceItem[] UnusedResources) : IStructuredToolResult;

    public sealed record XamlNamesResponse(
        string Summary,
        string FilePath,
        XamlNamedElementItem[] NamedElements,
        XamlDuplicateNameItem[] DuplicateNames) : IStructuredToolResult;

    public sealed record XamlStructureResponse(
        string Summary,
        string FilePath,
        XamlElementNode? Root,
        XamlDiagnosticItem[] ParseErrors) : IStructuredToolResult;

    public sealed record XamlBindingErrorsResponse(
        string Summary,
        string FilePath,
        XamlBindingIssueItem[] Issues) : IStructuredToolResult;

    public sealed record XamlExtractViewModelResponse(
        string Summary,
        string FilePath,
        string InterfaceName,
        int BindingCount,
        XamlViewModelPropertyItem[] Properties,
        string GeneratedCode,
        string[] Notes) : IStructuredToolResult;

    public sealed record XamlDiagnosticItem(
        string Severity,
        int Line,
        int Column,
        string Message,
        string? Code,
        string? Source);

    public sealed record XamlBindingItem(
        string Path,
        int Line,
        int Column,
        string? Mode,
        string? Converter,
        string? ElementName,
        string? Source,
        string? RelativeSource);

    public sealed record XamlResourceItem(
        string Key,
        string Type,
        int Line,
        int Column,
        bool IsStyle,
        string? TargetType);

    public sealed record XamlResourceReferenceItem(
        string Key,
        bool IsStatic,
        int Line,
        int Column);

    public sealed record XamlNamedElementItem(
        string Name,
        string Type,
        int Line,
        int Column);

    public sealed record XamlDuplicateNameItem(
        string Name,
        int[] Lines);

    public sealed record XamlElementNode(
        string Name,
        int Line,
        int Column,
        string? XName,
        XamlElementNode[] Children,
        bool IsTruncated);

    public sealed record XamlBindingIssueItem(
        string Severity,
        int Line,
        int Column,
        string Message);

    public sealed record XamlViewModelPropertyItem(
        string Name,
        string Type);
}
