using System.Text;
using System.Text.RegularExpressions;
using CSharpLspMcp.Workspace;

namespace CSharpLspMcp.Analysis.Architecture;

public sealed class CSharpSemanticSearchAnalysisService
{
    private static readonly string[] IgnoredPathSegments = [".git", ".idea", ".vs", "bin", "obj", "node_modules"];
    private static readonly Regex RouteRegex = new(@"\b\w+\.(?<method>Map(?:Get|Post|Put|Delete|Patch|Group))\s*\(", RegexOptions.Compiled);
    private static readonly Regex HostedServiceRegistrationRegex = new(@"AddHostedService<(?<type>[^>]+)>", RegexOptions.Compiled);
    private static readonly Regex BackgroundServiceImplementationRegex = new(
        @"class\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*:\s*BackgroundService\b",
        RegexOptions.Compiled);
    private static readonly Regex GenericRegistrationRegex = new(
        @"\b\w+\.(?<method>(?:TryAdd|Add)(?<lifetime>Scoped|Singleton|Transient))<(?<genericArgs>[^>]*)>\s*\((?<arguments>.*?)\)\s*;",
        RegexOptions.Compiled);
    private static readonly Regex EnumerableRegistrationRegex = new(
        @"\b\w+\.TryAddEnumerable\s*\(\s*ServiceDescriptor\.(?<lifetime>Singleton|Scoped|Transient)<(?<service>[^,>]+)\s*,\s*(?<implementation>[^>]+)>\s*\(\s*\)\s*\)",
        RegexOptions.Compiled);
    private static readonly Regex FactoryImplementationRegex = new(
        @"GetRequiredService<(?<implementation>[^>]+)>\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex NewImplementationRegex = new(
        @"new\s+(?<implementation>[A-Za-z_][A-Za-z0-9_<>,\.\?]*)\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex MiddlewareRegex = new(@"\bapp\.(?<method>Use[A-Z][A-Za-z0-9_]*)\s*\(", RegexOptions.Compiled);
    private static readonly Regex ConfigureRegex = new(@"Configure<(?<type>[^>]+)>\s*\(", RegexOptions.Compiled);
    private static readonly Regex AddOptionsRegex = new(@"AddOptions<(?<type>[^>]+)>", RegexOptions.Compiled);
    private readonly WorkspaceState _workspaceState;

    public CSharpSemanticSearchAnalysisService(WorkspaceState workspaceState)
    {
        _workspaceState = workspaceState;
    }

    public Task<string> SearchAsync(
        string query,
        string? projectFilter,
        bool includeTests,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var workspacePath = _workspaceState.CurrentPath;
        if (string.IsNullOrWhiteSpace(workspacePath))
            return Task.FromResult("Error: Workspace is not set. Call csharp_set_workspace first.");

        cancellationToken.ThrowIfCancellationRequested();

        var normalizedQuery = NormalizeQuery(query);
        var effectiveMaxResults = Math.Max(1, maxResults);

        SearchMatch[] matches = normalizedQuery switch
        {
            "aspnet_endpoints" => FindAspNetEndpoints(workspacePath).ToArray(),
            "hosted_services" => FindHostedServices(workspacePath).ToArray(),
            "di_registrations" => FindDiRegistrations(workspacePath).ToArray(),
            "config_bindings" => FindConfigurationBindings(workspacePath).ToArray(),
            "middleware_pipeline" => FindMiddlewarePipeline(workspacePath).ToArray(),
            _ => Array.Empty<SearchMatch>()
        };

        if (matches.Length == 0 && !IsSupportedQuery(normalizedQuery))
        {
            return Task.FromResult(
                $"Error: Unsupported semantic search query '{query}'. Supported queries: aspnet_endpoints, hosted_services, di_registrations, config_bindings, middleware_pipeline.");
        }

        var filteredMatches = matches
            .Where(match => includeTests || !IsTestPath(match.RelativePath))
            .Where(match => MatchesProjectFilter(match.RelativePath, projectFilter))
            .OrderBy(match => match.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(match => match.LineNumber)
            .ThenBy(match => match.Kind, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (filteredMatches.Length == 0)
        {
            return Task.FromResult(
                $"No semantic matches found for '{normalizedQuery}'{FormatProjectFilterSuffix(projectFilter)}.");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Semantic search: {normalizedQuery}");
        if (!string.IsNullOrWhiteSpace(projectFilter))
            sb.AppendLine($"Project filter: {projectFilter}");
        sb.AppendLine($"Include tests: {includeTests}");
        sb.AppendLine();
        sb.AppendLine($"Matches ({filteredMatches.Length}):");

        foreach (var match in filteredMatches.Take(effectiveMaxResults))
            sb.AppendLine($"• [{match.Kind}] {match.RelativePath}:{match.LineNumber} {match.Text}");

        if (filteredMatches.Length > effectiveMaxResults)
            sb.AppendLine($"... and {filteredMatches.Length - effectiveMaxResults} more");

        return Task.FromResult(sb.ToString().TrimEnd());
    }

    private static IEnumerable<SearchMatch> FindAspNetEndpoints(string workspacePath)
    {
        foreach (var filePath in EnumerateSourceFiles(workspacePath))
        {
            foreach (var match in FindLineMatches(workspacePath, filePath, RouteRegex, static regexMatch => regexMatch.Groups["method"].Value))
                yield return match;
        }
    }

    private static IEnumerable<SearchMatch> FindHostedServices(string workspacePath)
    {
        foreach (var filePath in EnumerateSourceFiles(workspacePath))
        {
            foreach (var match in FindLineMatches(workspacePath, filePath, HostedServiceRegistrationRegex, static regexMatch => $"registration:{NormalizeTypeName(regexMatch.Groups["type"].Value)}"))
                yield return match;

            foreach (var match in FindLineMatches(workspacePath, filePath, BackgroundServiceImplementationRegex, static regexMatch => $"implementation:{regexMatch.Groups["name"].Value}"))
                yield return match;
        }
    }

    private static IEnumerable<SearchMatch> FindDiRegistrations(string workspacePath)
    {
        foreach (var filePath in EnumerateSourceFiles(workspacePath))
        {
            foreach (var match in FindDiRegistrationMatches(workspacePath, filePath))
                yield return match;
        }
    }

    private static IEnumerable<SearchMatch> FindConfigurationBindings(string workspacePath)
    {
        foreach (var filePath in EnumerateSourceFiles(workspacePath))
        {
            foreach (var match in FindConfigurationBindingMatches(workspacePath, filePath))
                yield return match;
        }
    }

    private static IEnumerable<SearchMatch> FindMiddlewarePipeline(string workspacePath)
    {
        foreach (var filePath in EnumerateSourceFiles(workspacePath))
        {
            foreach (var match in FindLineMatches(workspacePath, filePath, MiddlewareRegex, static regexMatch => regexMatch.Groups["method"].Value))
                yield return match;
        }
    }

    private static IEnumerable<SearchMatch> FindDiRegistrationMatches(string workspacePath, string filePath)
    {
        var content = File.ReadAllText(filePath);
        foreach (Match match in GenericRegistrationRegex.Matches(content))
        {
            var genericArgs = SplitDelimited(match.Groups["genericArgs"].Value);
            if (genericArgs.Count == 0)
                continue;

            var serviceType = NormalizeTypeName(genericArgs[0]);
            var implementationType = genericArgs.Count > 1
                ? NormalizeTypeName(genericArgs[1])
                : ResolveSingleGenericImplementation(serviceType, match.Groups["arguments"].Value);
            var lineNumber = GetLineNumber(content, match.Index);
            yield return new SearchMatch(
                $"{match.Groups["lifetime"].Value}:{serviceType}",
                Path.GetRelativePath(workspacePath, filePath),
                lineNumber,
                NormalizeSourceText(match.Value) + $" -> {implementationType}");
        }

        foreach (Match match in EnumerableRegistrationRegex.Matches(content))
        {
            var lineNumber = GetLineNumber(content, match.Index);
            yield return new SearchMatch(
                $"enumerable:{NormalizeTypeName(match.Groups["service"].Value)}",
                Path.GetRelativePath(workspacePath, filePath),
                lineNumber,
                NormalizeSourceText(match.Value) + $" -> {NormalizeTypeName(match.Groups["implementation"].Value)}");
        }
    }

    private static IEnumerable<SearchMatch> FindConfigurationBindingMatches(string workspacePath, string filePath)
    {
        var relativePath = Path.GetRelativePath(workspacePath, filePath);
        string? pendingOptionsType = null;
        var pendingLineNumber = 0;
        var lineNumber = 0;

        foreach (var line in File.ReadLines(filePath))
        {
            lineNumber++;
            var trimmedLine = line.Trim();

            var configureMatch = ConfigureRegex.Match(trimmedLine);
            if (configureMatch.Success && trimmedLine.Contains("GetSection(", StringComparison.Ordinal))
            {
                yield return new SearchMatch(
                    $"configure:{NormalizeTypeName(configureMatch.Groups["type"].Value)}",
                    relativePath,
                    lineNumber,
                    trimmedLine);
            }

            var addOptionsMatch = AddOptionsRegex.Match(trimmedLine);
            if (addOptionsMatch.Success)
            {
                pendingOptionsType = NormalizeTypeName(addOptionsMatch.Groups["type"].Value);
                pendingLineNumber = lineNumber;
            }

            if (pendingOptionsType != null &&
                trimmedLine.Contains(".Bind(", StringComparison.Ordinal) &&
                trimmedLine.Contains("GetSection(", StringComparison.Ordinal))
            {
                yield return new SearchMatch(
                    $"bind:{pendingOptionsType}",
                    relativePath,
                    pendingLineNumber,
                    NormalizeSourceText(trimmedLine));
                pendingOptionsType = null;
                pendingLineNumber = 0;
            }

            if (pendingOptionsType != null && trimmedLine.EndsWith(';'))
            {
                pendingOptionsType = null;
                pendingLineNumber = 0;
            }
        }
    }

    private static IEnumerable<SearchMatch> FindLineMatches(
        string workspacePath,
        string filePath,
        Regex regex,
        Func<Match, string> kindSelector)
    {
        var lineNumber = 0;
        foreach (var line in File.ReadLines(filePath))
        {
            lineNumber++;
            var match = regex.Match(line);
            if (!match.Success)
                continue;

            yield return new SearchMatch(
                kindSelector(match),
                Path.GetRelativePath(workspacePath, filePath),
                lineNumber,
                line.Trim());
        }
    }

    private static IReadOnlyCollection<string> EnumerateSourceFiles(string workspacePath)
    {
        return Directory
            .EnumerateFiles(workspacePath, "*.cs", SearchOption.AllDirectories)
            .Where(path => !ContainsIgnoredPathSegment(path))
            .ToArray();
    }

    private static IReadOnlyList<string> SplitDelimited(string value)
    {
        var items = new List<string>();
        var current = new StringBuilder();
        var angleDepth = 0;
        var parenthesisDepth = 0;
        var bracketDepth = 0;

        foreach (var character in value)
        {
            switch (character)
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    angleDepth = Math.Max(0, angleDepth - 1);
                    break;
                case '(':
                    parenthesisDepth++;
                    break;
                case ')':
                    parenthesisDepth = Math.Max(0, parenthesisDepth - 1);
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    bracketDepth = Math.Max(0, bracketDepth - 1);
                    break;
                case ',' when angleDepth == 0 && parenthesisDepth == 0 && bracketDepth == 0:
                    items.Add(current.ToString().Trim());
                    current.Clear();
                    continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
            items.Add(current.ToString().Trim());

        return items.Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
    }

    private static int GetLineNumber(string content, int index)
        => content.Take(index).Count(character => character == '\n') + 1;

    private static bool ContainsIgnoredPathSegment(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment => IgnoredPathSegments.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsTestPath(string relativePath)
    {
        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment =>
                   string.Equals(segment, "test", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(segment, "tests", StringComparison.OrdinalIgnoreCase)) ||
               Path.GetFileName(relativePath).Contains("Test", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesProjectFilter(string relativePath, string? projectFilter)
    {
        if (string.IsNullOrWhiteSpace(projectFilter))
            return true;

        return relativePath.Contains(projectFilter.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeQuery(string query)
        => query.Trim().ToLowerInvariant();

    private static bool IsSupportedQuery(string query)
        => query is "aspnet_endpoints" or "hosted_services" or "di_registrations" or "config_bindings" or "middleware_pipeline";

    private static string FormatProjectFilterSuffix(string? projectFilter)
        => string.IsNullOrWhiteSpace(projectFilter) ? string.Empty : $" in '{projectFilter}'";

    private static string NormalizeSourceText(string value)
        => Regex.Replace(value, @"\s+", " ").Trim();

    private static string NormalizeTypeName(string value)
        => value.Replace("global::", string.Empty, StringComparison.Ordinal).Trim().TrimEnd('?');

    private static string ResolveSingleGenericImplementation(string serviceType, string arguments)
    {
        var requiredServiceMatch = FactoryImplementationRegex.Match(arguments);
        if (requiredServiceMatch.Success)
            return NormalizeTypeName(requiredServiceMatch.Groups["implementation"].Value);

        var newMatch = NewImplementationRegex.Match(arguments);
        if (newMatch.Success)
            return NormalizeTypeName(newMatch.Groups["implementation"].Value);

        return serviceType;
    }

    private sealed record SearchMatch(
        string Kind,
        string RelativePath,
        int LineNumber,
        string Text);
}
