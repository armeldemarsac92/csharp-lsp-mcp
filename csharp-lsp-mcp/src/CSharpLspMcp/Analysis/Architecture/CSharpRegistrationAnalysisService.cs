using System.Text;
using System.Text.RegularExpressions;
using CSharpLspMcp.Contracts.Common;
using CSharpLspMcp.Workspace;

namespace CSharpLspMcp.Analysis.Architecture;

public sealed class CSharpRegistrationAnalysisService
{
    private static readonly string[] IgnoredPathSegments = [".git", ".idea", ".vs", "bin", "obj", "node_modules"];
    private static readonly Regex GenericRegistrationRegex = new(
        @"\b\w+\.(?<method>(?:TryAdd|Add)(?<lifetime>Scoped|Singleton|Transient))<(?<genericArgs>[^>]*)>\s*\((?<arguments>.*?)\)\s*;",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex EnumerableRegistrationRegex = new(
        @"\b\w+\.TryAddEnumerable\s*\(\s*ServiceDescriptor\.(?<lifetime>Singleton|Scoped|Transient)<(?<service>[^,>]+)\s*,\s*(?<implementation>[^>]+)>\s*\(\s*\)\s*\)\s*;",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex FactoryImplementationRegex = new(
        @"GetRequiredService<(?<implementation>[^>]+)>\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex NewImplementationRegex = new(
        @"new\s+(?<implementation>[A-Za-z_][A-Za-z0-9_<>,\.\?]*)\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex TypeDeclarationWithPrimaryConstructorRegex = new(
        @"(?<prefix>\b(?:public|internal|private|protected|file|sealed|abstract|partial|static)\s+)*(?:class|record)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\((?<parameters>[^)]*)\)",
        RegexOptions.Compiled);
    private static readonly Regex ConstructorRegex = new(
        @"(?<prefix>\b(?:public|internal|private|protected)\s+)(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\((?<parameters>[^)]*)\)",
        RegexOptions.Compiled);
    private readonly WorkspaceState _workspaceState;

    public CSharpRegistrationAnalysisService(WorkspaceState workspaceState)
    {
        _workspaceState = workspaceState;
    }

    public Task<RegistrationAnalysisResponse> FindRegistrationsAsync(
        string? query,
        bool includeConsumers,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var workspacePath = _workspaceState.CurrentPath;
        if (string.IsNullOrWhiteSpace(workspacePath))
            throw new InvalidOperationException("Workspace is not set. Call csharp_set_workspace first.");

        cancellationToken.ThrowIfCancellationRequested();

        var effectiveMaxResults = Math.Max(1, maxResults);
        var registrations = FindRegistrations(workspacePath)
            .Where(registration => MatchesQuery(registration, query))
            .OrderBy(registration => registration.ServiceType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(registration => registration.ImplementationType ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(registration => registration.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(registration => registration.LineNumber)
            .ToArray();

        if (registrations.Length == 0)
        {
            return Task.FromResult(
                new RegistrationAnalysisResponse(
                    Summary: string.IsNullOrWhiteSpace(query)
                        ? "No DI registrations were found under the current workspace."
                        : $"No DI registrations matched '{query}'.",
                    SolutionRoot: workspacePath,
                    Query: query,
                    IncludeConsumers: includeConsumers,
                    TotalRegistrations: 0,
                    Registrations: Array.Empty<RegistrationItem>(),
                    TruncatedRegistrations: 0));
        }

        var consumers = includeConsumers
            ? FindConsumers(workspacePath)
            : Array.Empty<ConsumerSite>();

        var items = registrations
            .Take(effectiveMaxResults)
            .Select(registration =>
            {
                var matchingConsumers = includeConsumers
                    ? consumers
                        .Where(consumer => RegistrationMatchesConsumer(registration, consumer))
                        .OrderBy(consumer => consumer.RelativePath, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(consumer => consumer.LineNumber)
                        .ToArray()
                    : Array.Empty<ConsumerSite>();

                return new RegistrationItem(
                    registration.ServiceType,
                    registration.ImplementationType,
                    registration.Lifetime,
                    registration.RelativePath,
                    registration.LineNumber,
                    registration.SourceText,
                    registration.IsFactory,
                    registration.IsEnumerable,
                    matchingConsumers.Take(effectiveMaxResults)
                        .Select(consumer => new ConsumerItem(
                            consumer.RelativePath,
                            consumer.LineNumber,
                            consumer.DisplayText,
                            consumer.ParameterTypes))
                        .ToArray(),
                    Math.Max(0, matchingConsumers.Length - effectiveMaxResults));
            })
            .ToArray();

        return Task.FromResult(
            new RegistrationAnalysisResponse(
                Summary: $"Found {registrations.Length} DI registration(s){(includeConsumers ? " with consumer tracing" : string.Empty)}.",
                SolutionRoot: workspacePath,
                Query: query,
                IncludeConsumers: includeConsumers,
                TotalRegistrations: registrations.Length,
                Registrations: items,
                TruncatedRegistrations: Math.Max(0, registrations.Length - effectiveMaxResults)));
    }

    private static IReadOnlyCollection<RegistrationSite> FindRegistrations(string workspacePath)
    {
        var registrations = new List<RegistrationSite>();

        foreach (var filePath in EnumerateSourceFiles(workspacePath))
        {
            var content = File.ReadAllText(filePath);
            registrations.AddRange(ParseGenericRegistrations(workspacePath, filePath, content));
            registrations.AddRange(ParseEnumerableRegistrations(workspacePath, filePath, content));
        }

        return registrations
            .DistinctBy(registration => $"{registration.ServiceType}|{registration.ImplementationType}|{registration.RelativePath}|{registration.LineNumber}|{registration.SourceText}")
            .ToArray();
    }

    private static IEnumerable<RegistrationSite> ParseGenericRegistrations(string workspacePath, string filePath, string content)
    {
        foreach (Match match in GenericRegistrationRegex.Matches(content))
        {
            var genericArguments = SplitDelimited(match.Groups["genericArgs"].Value);
            if (genericArguments.Count == 0)
                continue;

            var serviceType = NormalizeTypeName(genericArguments[0]);
            var implementationType = genericArguments.Count > 1
                ? NormalizeTypeName(genericArguments[1])
                : ResolveSingleGenericImplementation(serviceType, match.Groups["arguments"].Value);
            var sourceText = NormalizeSourceText(match.Value);
            var lineNumber = GetLineNumber(content, match.Index);

            yield return new RegistrationSite(
                serviceType,
                implementationType,
                match.Groups["lifetime"].Value,
                Path.GetRelativePath(workspacePath, filePath),
                lineNumber,
                sourceText,
                IsFactoryRegistration(genericArguments.Count, implementationType, serviceType, match.Groups["arguments"].Value),
                false);
        }
    }

    private static IEnumerable<RegistrationSite> ParseEnumerableRegistrations(string workspacePath, string filePath, string content)
    {
        foreach (Match match in EnumerableRegistrationRegex.Matches(content))
        {
            var lineNumber = GetLineNumber(content, match.Index);
            yield return new RegistrationSite(
                NormalizeTypeName(match.Groups["service"].Value),
                NormalizeTypeName(match.Groups["implementation"].Value),
                match.Groups["lifetime"].Value,
                Path.GetRelativePath(workspacePath, filePath),
                lineNumber,
                NormalizeSourceText(match.Value),
                false,
                true);
        }
    }

    private static IReadOnlyCollection<ConsumerSite> FindConsumers(string workspacePath)
    {
        var consumers = new List<ConsumerSite>();

        foreach (var filePath in EnumerateSourceFiles(workspacePath))
        {
            var content = File.ReadAllText(filePath);
            consumers.AddRange(ParsePrimaryConstructorConsumers(workspacePath, filePath, content));
            consumers.AddRange(ParseConstructorConsumers(workspacePath, filePath, content));
        }

        return consumers
            .DistinctBy(consumer => $"{consumer.RelativePath}|{consumer.LineNumber}|{consumer.DisplayText}")
            .ToArray();
    }

    private static IEnumerable<ConsumerSite> ParsePrimaryConstructorConsumers(string workspacePath, string filePath, string content)
    {
        foreach (Match match in TypeDeclarationWithPrimaryConstructorRegex.Matches(content))
        {
            var consumerName = match.Groups["name"].Value;
            var parameterTypes = ExtractParameterTypes(match.Groups["parameters"].Value);
            if (parameterTypes.Length == 0)
                continue;

            yield return new ConsumerSite(
                Path.GetRelativePath(workspacePath, filePath),
                GetLineNumber(content, match.Index),
                $"{consumerName}({string.Join(", ", parameterTypes)})",
                parameterTypes);
        }
    }

    private static IEnumerable<ConsumerSite> ParseConstructorConsumers(string workspacePath, string filePath, string content)
    {
        foreach (Match match in ConstructorRegex.Matches(content))
        {
            var consumerName = match.Groups["name"].Value;
            var parameterTypes = ExtractParameterTypes(match.Groups["parameters"].Value);
            if (parameterTypes.Length == 0)
                continue;

            yield return new ConsumerSite(
                Path.GetRelativePath(workspacePath, filePath),
                GetLineNumber(content, match.Index),
                $"{consumerName}({string.Join(", ", parameterTypes)})",
                parameterTypes);
        }
    }

    private static string[] ExtractParameterTypes(string parameters)
    {
        return SplitDelimited(parameters)
            .Select(ExtractParameterType)
            .Where(typeName => !string.IsNullOrWhiteSpace(typeName))
            .Select(NormalizeTypeName)
            .ToArray();
    }

    private static string ExtractParameterType(string parameter)
    {
        var cleanedParameter = parameter.Trim();
        if (string.IsNullOrWhiteSpace(cleanedParameter))
            return string.Empty;

        cleanedParameter = Regex.Replace(cleanedParameter, @"\[[^\]]+\]\s*", string.Empty);
        var defaultValueIndex = cleanedParameter.IndexOf('=');
        if (defaultValueIndex >= 0)
            cleanedParameter = cleanedParameter[..defaultValueIndex].Trim();

        var tokens = cleanedParameter
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token is not "this" and not "ref" and not "out" and not "in" and not "params")
            .ToArray();

        if (tokens.Length <= 1)
            return string.Empty;

        return string.Join(" ", tokens[..^1]);
    }

    private static bool RegistrationMatchesConsumer(RegistrationSite registration, ConsumerSite consumer)
    {
        return consumer.ParameterTypes.Any(parameterType =>
            TypeExpressionContains(parameterType, registration.ServiceType) ||
            (!string.IsNullOrWhiteSpace(registration.ImplementationType) &&
             TypeExpressionContains(parameterType, registration.ImplementationType)));
    }

    private static bool TypeExpressionContains(string parameterType, string candidateType)
    {
        var normalizedParameterType = NormalizeTypeExpression(parameterType);
        var normalizedCandidateType = NormalizeTypeExpression(candidateType);
        var simpleCandidateType = NormalizeTypeExpression(GetSimpleTypeName(candidateType));

        if (string.Equals(normalizedParameterType, normalizedCandidateType, StringComparison.Ordinal) ||
            string.Equals(normalizedParameterType, simpleCandidateType, StringComparison.Ordinal))
            return true;

        foreach (var wrapper in SupportedCollectionWrappers)
        {
            if (string.Equals(normalizedParameterType, $"{wrapper}<{normalizedCandidateType}>", StringComparison.Ordinal) ||
                string.Equals(normalizedParameterType, $"{wrapper}<{simpleCandidateType}>", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return string.Equals(normalizedParameterType, $"{normalizedCandidateType}[]", StringComparison.Ordinal) ||
               string.Equals(normalizedParameterType, $"{simpleCandidateType}[]", StringComparison.Ordinal);
    }

    private static bool MatchesQuery(RegistrationSite registration, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        var trimmedQuery = query.Trim();
        return registration.ServiceType.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase) ||
               (registration.ImplementationType?.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase) ?? false) ||
               registration.RelativePath.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase) ||
               registration.SourceText.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveSingleGenericImplementation(string serviceType, string arguments)
    {
        var requiredServiceMatch = FactoryImplementationRegex.Match(arguments);
        if (requiredServiceMatch.Success)
            return NormalizeTypeName(requiredServiceMatch.Groups["implementation"].Value);

        var newMatch = NewImplementationRegex.Match(arguments);
        if (newMatch.Success)
            return NormalizeTypeName(newMatch.Groups["implementation"].Value);

        if (LooksLikeLambda(arguments))
            return "factory";

        return serviceType;
    }

    private static bool IsFactoryRegistration(int genericArgumentCount, string? implementationType, string serviceType, string arguments)
    {
        if (genericArgumentCount > 1)
            return false;

        return LooksLikeLambda(arguments) || !string.Equals(implementationType, serviceType, StringComparison.Ordinal);
    }

    private static bool LooksLikeLambda(string arguments)
        => arguments.Contains("=>", StringComparison.Ordinal);

    private static int GetLineNumber(string content, int index)
        => content.Take(index).Count(character => character == '\n') + 1;

    private static IReadOnlyCollection<string> EnumerateSourceFiles(string workspacePath)
    {
        return Directory
            .EnumerateFiles(workspacePath, "*.cs", SearchOption.AllDirectories)
            .Where(path => !ContainsIgnoredPathSegment(path))
            .ToArray();
    }

    private static bool ContainsIgnoredPathSegment(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment => IgnoredPathSegments.Contains(segment, StringComparer.OrdinalIgnoreCase));
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

    private static string NormalizeSourceText(string value)
        => Regex.Replace(value, @"\s+", " ").Trim();

    private static string NormalizeTypeName(string value)
        => value
            .Replace("global::", string.Empty, StringComparison.Ordinal)
            .Trim()
            .TrimEnd('?');

    private static string NormalizeTypeExpression(string value)
        => NormalizeTypeName(value)
            .Replace(" ", string.Empty, StringComparison.Ordinal);

    private static IReadOnlyCollection<string> SupportedCollectionWrappers { get; } =
        ["IEnumerable", "IReadOnlyCollection", "IReadOnlyList", "ICollection", "IList", "List"];

    private static string GetSimpleTypeName(string value)
    {
        var normalizedValue = NormalizeTypeName(value);
        var lastDotIndex = normalizedValue.LastIndexOf('.');
        return lastDotIndex >= 0
            ? normalizedValue[(lastDotIndex + 1)..]
            : normalizedValue;
    }

    private sealed record RegistrationSite(
        string ServiceType,
        string? ImplementationType,
        string Lifetime,
        string RelativePath,
        int LineNumber,
        string SourceText,
        bool IsFactory,
        bool IsEnumerable);

    private sealed record ConsumerSite(
        string RelativePath,
        int LineNumber,
        string DisplayText,
        string[] ParameterTypes);

    public sealed record RegistrationAnalysisResponse(
        string Summary,
        string SolutionRoot,
        string? Query,
        bool IncludeConsumers,
        int TotalRegistrations,
        RegistrationItem[] Registrations,
        int TruncatedRegistrations) : IStructuredToolResult;

    public sealed record RegistrationItem(
        string ServiceType,
        string? ImplementationType,
        string Lifetime,
        string RelativePath,
        int LineNumber,
        string SourceText,
        bool IsFactory,
        bool IsEnumerable,
        ConsumerItem[] Consumers,
        int TruncatedConsumers);

    public sealed record ConsumerItem(
        string RelativePath,
        int LineNumber,
        string DisplayText,
        string[] ParameterTypes);
}
