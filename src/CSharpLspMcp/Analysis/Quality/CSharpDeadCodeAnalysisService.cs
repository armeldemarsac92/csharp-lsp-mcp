using System.Text.RegularExpressions;
using CSharpLspMcp.Contracts.Common;
using CSharpLspMcp.Workspace;

namespace CSharpLspMcp.Analysis.Quality;

public sealed class CSharpDeadCodeAnalysisService
{
    private static readonly string[] IgnoredPathSegments = [".git", ".idea", ".vs", "bin", "obj", "node_modules"];
    private static readonly string[] IgnoredFileSuffixes = [".g.cs", ".designer.cs", ".generated.cs"];
    private static readonly Regex IdentifierRegex = new(@"\b[A-Za-z_][A-Za-z0-9_]*\b", RegexOptions.Compiled);
    private static readonly Regex PrivateMethodRegex = new(
        @"(?<declaration>\bprivate\s+(?:(?:static|async|unsafe|extern|new|partial|virtual|sealed|override)\s+)*(?<returnType>[A-Za-z_][A-Za-z0-9_<>,\.\[\]\?\s:]*)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\()",
        RegexOptions.Compiled);
    private static readonly Regex PrivateFieldRegex = new(
        @"(?<declaration>\bprivate\s+(?:(?:static|readonly|volatile|const|required|unsafe|new)\s+)*(?<type>[A-Za-z_][A-Za-z0-9_<>,\.\[\]\?]*)\s+(?<name>_?[A-Za-z_][A-Za-z0-9_]*)\s*(?:=|;))",
        RegexOptions.Compiled);
    private static readonly Regex InternalTypeRegex = new(
        @"(?<declaration>\binternal\s+(?:(?:sealed|abstract|static|partial)\s+)*(?<kind>class|record|interface|struct|enum)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\b)",
        RegexOptions.Compiled);
    private static readonly Regex StaticMemberMethodRegex = new(
        @"\b(?:public|internal|private)\s+static\s+[A-Za-z_][A-Za-z0-9_<>,\.\[\]\?\s:]*\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(",
        RegexOptions.Compiled);
    private readonly WorkspaceState _workspaceState;

    public CSharpDeadCodeAnalysisService(WorkspaceState workspaceState)
    {
        _workspaceState = workspaceState;
    }

    public Task<DeadCodeAnalysisResponse> FindDeadCodeCandidatesAsync(
        bool includePrivateMembers,
        bool includeInternalTypes,
        bool includeTests,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var workspacePath = _workspaceState.CurrentPath;
        if (string.IsNullOrWhiteSpace(workspacePath))
            throw new InvalidOperationException("Workspace is not set. Call csharp_set_workspace first.");

        cancellationToken.ThrowIfCancellationRequested();

        var workspaceFiles = LoadWorkspaceFiles(workspacePath, includeTests, cancellationToken);
        var identifierCounts = BuildIdentifierCounts(workspaceFiles.Values, cancellationToken);
        var candidates = new List<DeadCodeCandidate>();

        if (includePrivateMembers)
        {
            candidates.AddRange(FindPrivateMethodCandidates(workspacePath, workspaceFiles, identifierCounts));
            candidates.AddRange(FindPrivateFieldCandidates(workspacePath, workspaceFiles, identifierCounts));
        }

        if (includeInternalTypes)
            candidates.AddRange(FindInternalTypeCandidates(workspacePath, workspaceFiles, identifierCounts));

        if (candidates.Count == 0)
        {
            return Task.FromResult(
                new DeadCodeAnalysisResponse(
                    Summary: "No dead code candidates were found.",
                    SolutionRoot: workspacePath,
                    IncludePrivateMembers: includePrivateMembers,
                    IncludeInternalTypes: includeInternalTypes,
                    IncludeTests: includeTests,
                    Note: "Best-effort heuristic. Reflection, source generation, XAML, analyzers, or string-based access may hide real usages.",
                    TotalCandidates: 0,
                    Candidates: Array.Empty<DeadCodeCandidateItem>(),
                    TruncatedCandidates: 0));
        }

        var effectiveMaxResults = Math.Max(1, maxResults);
        var orderedCandidates = candidates
            .OrderBy(candidate => candidate.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.LineNumber)
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult(
            new DeadCodeAnalysisResponse(
                Summary: $"Found {orderedCandidates.Length} dead-code candidate(s).",
                SolutionRoot: workspacePath,
                IncludePrivateMembers: includePrivateMembers,
                IncludeInternalTypes: includeInternalTypes,
                IncludeTests: includeTests,
                Note: "Best-effort heuristic. Reflection, source generation, XAML, analyzers, or string-based access may hide real usages.",
                TotalCandidates: orderedCandidates.Length,
                Candidates: orderedCandidates.Take(effectiveMaxResults)
                    .Select(candidate => new DeadCodeCandidateItem(
                        candidate.Kind,
                        candidate.Name,
                        candidate.RelativePath,
                        candidate.LineNumber,
                        candidate.SourceText,
                        candidate.Evidence))
                    .ToArray(),
                TruncatedCandidates: Math.Max(0, orderedCandidates.Length - effectiveMaxResults)));
    }

    private static IReadOnlyDictionary<string, string> LoadWorkspaceFiles(string workspacePath, bool includeTests, CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var filePath in Directory.EnumerateFiles(workspacePath, "*.cs", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ContainsIgnoredPathSegment(filePath) || HasIgnoredFileSuffix(filePath))
                continue;

            if (!includeTests && IsTestFile(filePath))
                continue;

            files[filePath] = File.ReadAllText(filePath);
        }

        return files;
    }

    private static IReadOnlyDictionary<string, int> BuildIdentifierCounts(IEnumerable<string> fileContents, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var content in fileContents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (Match match in IdentifierRegex.Matches(content))
            {
                counts.TryGetValue(match.Value, out var count);
                counts[match.Value] = count + 1;
            }
        }

        return counts;
    }

    private static IEnumerable<DeadCodeCandidate> FindPrivateMethodCandidates(
        string workspacePath,
        IReadOnlyDictionary<string, string> workspaceFiles,
        IReadOnlyDictionary<string, int> identifierCounts)
    {
        foreach (var (filePath, content) in workspaceFiles)
        {
            foreach (Match match in PrivateMethodRegex.Matches(content))
            {
                var methodName = match.Groups["name"].Value;
                if (!HasOnlyDeclarationOccurrence(methodName, identifierCounts))
                    continue;

                yield return CreateCandidate(
                    workspacePath,
                    filePath,
                    content,
                    match.Index,
                    "private-method",
                    methodName,
                    "identifier appears only in its declaration");
            }
        }
    }

    private static IEnumerable<DeadCodeCandidate> FindPrivateFieldCandidates(
        string workspacePath,
        IReadOnlyDictionary<string, string> workspaceFiles,
        IReadOnlyDictionary<string, int> identifierCounts)
    {
        foreach (var (filePath, content) in workspaceFiles)
        {
            foreach (Match match in PrivateFieldRegex.Matches(content))
            {
                var fieldName = match.Groups["name"].Value;
                if (!HasOnlyDeclarationOccurrence(fieldName, identifierCounts))
                    continue;

                yield return CreateCandidate(
                    workspacePath,
                    filePath,
                    content,
                    match.Index,
                    "private-field",
                    fieldName,
                    "identifier appears only in its declaration");
            }
        }
    }

    private static IEnumerable<DeadCodeCandidate> FindInternalTypeCandidates(
        string workspacePath,
        IReadOnlyDictionary<string, string> workspaceFiles,
        IReadOnlyDictionary<string, int> identifierCounts)
    {
        foreach (var (filePath, content) in workspaceFiles)
        {
            foreach (Match match in InternalTypeRegex.Matches(content))
            {
                var typeName = match.Groups["name"].Value;
                if (!HasOnlyDeclarationOccurrence(typeName, identifierCounts))
                    continue;

                if (IsStaticTypeReferencedThroughMembers(content, match, identifierCounts))
                    continue;

                yield return CreateCandidate(
                    workspacePath,
                    filePath,
                    content,
                    match.Index,
                    "internal-type",
                    typeName,
                    "type name appears only in its declaration");
            }
        }
    }

    private static DeadCodeCandidate CreateCandidate(
        string workspacePath,
        string filePath,
        string content,
        int matchIndex,
        string kind,
        string name,
        string evidence)
        => new(
            kind,
            name,
            Path.GetRelativePath(workspacePath, filePath),
            GetLineNumber(content, matchIndex),
            GetSourceLine(content, matchIndex),
            evidence);

    private static bool IsStaticTypeReferencedThroughMembers(
        string content,
        Match typeMatch,
        IReadOnlyDictionary<string, int> identifierCounts)
    {
        if (!typeMatch.Value.Contains("static", StringComparison.Ordinal))
            return false;

        var body = TryExtractTypeBody(content, typeMatch.Index + typeMatch.Length);
        if (body == null)
            return false;

        foreach (Match memberMatch in StaticMemberMethodRegex.Matches(body))
        {
            var memberName = memberMatch.Groups["name"].Value;
            if (!HasOnlyDeclarationOccurrence(memberName, identifierCounts))
                return true;
        }

        return false;
    }

    private static bool HasOnlyDeclarationOccurrence(string identifier, IReadOnlyDictionary<string, int> identifierCounts)
        => identifierCounts.TryGetValue(identifier, out var count) && count == 1;

    private static bool ContainsIgnoredPathSegment(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment => IgnoredPathSegments.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }

    private static bool HasIgnoredFileSuffix(string filePath)
        => IgnoredFileSuffixes.Any(suffix => filePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    private static bool IsTestFile(string filePath)
    {
        var relativeSegments = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return relativeSegments.Any(segment =>
                   string.Equals(segment, "test", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(segment, "tests", StringComparison.OrdinalIgnoreCase)) ||
               Path.GetFileName(filePath).Contains("Test", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetLineNumber(string content, int index)
    {
        var lineNumber = 1;
        for (var i = 0; i < index && i < content.Length; i++)
        {
            if (content[i] == '\n')
                lineNumber++;
        }

        return lineNumber;
    }

    private static string GetSourceLine(string content, int index)
    {
        var lineStart = index;
        while (lineStart > 0 && content[lineStart - 1] != '\n')
            lineStart--;

        var lineEnd = index;
        while (lineEnd < content.Length && content[lineEnd] != '\n')
            lineEnd++;

        return content[lineStart..lineEnd].Trim();
    }

    private static string? TryExtractTypeBody(string content, int startIndex)
    {
        var openingBraceIndex = content.IndexOf('{', startIndex);
        if (openingBraceIndex < 0)
            return null;

        var depth = 0;
        for (var i = openingBraceIndex; i < content.Length; i++)
        {
            if (content[i] == '{')
                depth++;
            else if (content[i] == '}')
                depth--;

            if (depth == 0)
                return content[(openingBraceIndex + 1)..i];
        }

        return null;
    }

    private sealed record DeadCodeCandidate(
        string Kind,
        string Name,
        string RelativePath,
        int LineNumber,
        string SourceText,
        string Evidence);

    public sealed record DeadCodeAnalysisResponse(
        string Summary,
        string SolutionRoot,
        bool IncludePrivateMembers,
        bool IncludeInternalTypes,
        bool IncludeTests,
        string Note,
        int TotalCandidates,
        DeadCodeCandidateItem[] Candidates,
        int TruncatedCandidates) : IStructuredToolResult;

    public sealed record DeadCodeCandidateItem(
        string Kind,
        string Name,
        string RelativePath,
        int LineNumber,
        string SourceText,
        string Evidence);
}
