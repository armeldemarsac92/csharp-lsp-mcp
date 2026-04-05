using System.Text.RegularExpressions;
using CSharpLspMcp.Contracts.Common;
using CSharpLspMcp.Workspace;

namespace CSharpLspMcp.Analysis.Testing;

public sealed class CSharpTestMapAnalysisService
{
    private static readonly string[] IgnoredPathSegments = [".git", ".idea", ".vs", "bin", "obj", "node_modules"];
    private static readonly Regex TypeDeclarationRegex = new(
        @"\b(?:class|record|interface|struct)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);
    private readonly CSharpWorkspaceSession _workspaceSession;
    private readonly WorkspaceState _workspaceState;

    public CSharpTestMapAnalysisService(
        CSharpWorkspaceSession workspaceSession,
        WorkspaceState workspaceState)
    {
        _workspaceSession = workspaceSession;
        _workspaceState = workspaceState;
    }

    public Task<TestMapResponse> GetTestMapAsync(
        string? filePath,
        string? symbolQuery,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var workspacePath = _workspaceState.CurrentPath;
        if (string.IsNullOrWhiteSpace(workspacePath))
            throw new InvalidOperationException("Workspace is not set. Call csharp_set_workspace first.");

        cancellationToken.ThrowIfCancellationRequested();

        var effectiveMaxResults = Math.Max(1, maxResults);
        var target = ResolveTarget(filePath, symbolQuery);
        if (target == null)
            throw new InvalidOperationException("Provide either filePath or symbolQuery.");

        var relatedTests = FindRelatedTests(workspacePath, target, cancellationToken)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (relatedTests.Length == 0)
        {
            return Task.FromResult(
                new TestMapResponse(
                    Summary: $"No related tests found for {target.DisplayName}.",
                    SolutionRoot: workspacePath,
                    Target: target.DisplayName,
                    CandidateNames: target.CandidateNames,
                    TotalMatches: 0,
                    Matches: Array.Empty<TestMapMatchItem>(),
                    TruncatedMatches: 0));
        }

        return Task.FromResult(
            new TestMapResponse(
                Summary: $"Found {relatedTests.Length} related test candidate(s) for {target.DisplayName}.",
                SolutionRoot: workspacePath,
                Target: target.DisplayName,
                CandidateNames: target.CandidateNames,
                TotalMatches: relatedTests.Length,
                Matches: relatedTests.Take(effectiveMaxResults)
                    .Select(match => new TestMapMatchItem(
                        match.RelativePath,
                        match.Score,
                        match.Reasons,
                        match.LineMatches.Take(3)
                            .Select(lineMatch => new TestLineMatchItem(lineMatch.LineNumber, lineMatch.Text))
                            .ToArray(),
                        Math.Max(0, match.LineMatches.Length - 3)))
                    .ToArray(),
                TruncatedMatches: Math.Max(0, relatedTests.Length - effectiveMaxResults)));
    }

    private TargetInfo? ResolveTarget(string? filePath, string? symbolQuery)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var absolutePath = _workspaceSession.GetAbsolutePath(filePath);
            if (!File.Exists(absolutePath))
                return new TargetInfo(Path.GetFileName(filePath), Array.Empty<string>());

            var relativePath = FormatRelativePath(absolutePath);
            var content = File.ReadAllText(absolutePath);
            var candidateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.GetFileNameWithoutExtension(absolutePath)
            };

            foreach (Match match in TypeDeclarationRegex.Matches(content))
                candidateNames.Add(match.Groups["name"].Value);

            return new TargetInfo(relativePath, candidateNames.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray());
        }

        if (!string.IsNullOrWhiteSpace(symbolQuery))
        {
            var trimmedQuery = symbolQuery.Trim();
            var simpleName = GetTrailingIdentifier(trimmedQuery);
            var candidateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                trimmedQuery,
                simpleName
            };

            return new TargetInfo(trimmedQuery, candidateNames.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray());
        }

        return null;
    }

    private IEnumerable<TestMatch> FindRelatedTests(string workspacePath, TargetInfo target, CancellationToken cancellationToken)
    {
        foreach (var testFilePath in EnumerateTestFiles(workspacePath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(workspacePath, testFilePath);
            var fileName = Path.GetFileNameWithoutExtension(testFilePath);
            var content = File.ReadAllText(testFilePath);
            var lineMatches = FindLineMatches(content, target.CandidateNames).ToArray();
            var reasons = new List<string>();
            var score = 0;

            foreach (var candidateName in target.CandidateNames)
            {
                if (string.Equals(fileName, $"{candidateName}Tests", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fileName, $"{candidateName}Test", StringComparison.OrdinalIgnoreCase))
                {
                    score += 100;
                    reasons.Add($"file:{candidateName}");
                }
                else if (fileName.Contains(candidateName, StringComparison.OrdinalIgnoreCase))
                {
                    score += 60;
                    reasons.Add($"file-contains:{candidateName}");
                }
            }

            if (lineMatches.Length > 0)
            {
                score += Math.Min(80, lineMatches.Length * 10);
                reasons.Add($"content:{lineMatches.Length}");
            }

            if (score == 0)
                continue;

            yield return new TestMatch(relativePath, score, reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), lineMatches);
        }
    }

    private static IEnumerable<LineMatch> FindLineMatches(string content, IReadOnlyCollection<string> candidateNames)
    {
        var lineNumber = 0;
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;

            foreach (var candidateName in candidateNames)
            {
                if (!line.Contains(candidateName, StringComparison.OrdinalIgnoreCase))
                    continue;

                yield return new LineMatch(lineNumber, line.Trim());
                break;
            }
        }
    }

    private IEnumerable<string> EnumerateTestFiles(string workspacePath)
    {
        return Directory
            .EnumerateFiles(workspacePath, "*.cs", SearchOption.AllDirectories)
            .Where(path => !ContainsIgnoredPathSegment(path))
            .Where(IsTestFile);
    }

    private static bool IsTestFile(string filePath)
    {
        var relativeSegments = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return relativeSegments.Any(segment =>
                   string.Equals(segment, "test", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(segment, "tests", StringComparison.OrdinalIgnoreCase)) ||
               Path.GetFileName(filePath).Contains("Test", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsIgnoredPathSegment(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment => IgnoredPathSegments.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }

    private string FormatRelativePath(string absolutePath)
    {
        var workspacePath = _workspaceState.CurrentPath;
        return string.IsNullOrWhiteSpace(workspacePath)
            ? absolutePath
            : Path.GetRelativePath(workspacePath, absolutePath);
    }

    private static string GetTrailingIdentifier(string value)
    {
        var lastSeparator = value.LastIndexOf('.');
        return lastSeparator >= 0 && lastSeparator < value.Length - 1
            ? value[(lastSeparator + 1)..]
            : value;
    }

    private sealed record TargetInfo(
        string DisplayName,
        string[] CandidateNames);

    private sealed record TestMatch(
        string RelativePath,
        int Score,
        string[] Reasons,
        LineMatch[] LineMatches);

    private sealed record LineMatch(
        int LineNumber,
        string Text);

    public sealed record TestMapResponse(
        string Summary,
        string SolutionRoot,
        string Target,
        string[] CandidateNames,
        int TotalMatches,
        TestMapMatchItem[] Matches,
        int TruncatedMatches) : IStructuredToolResult;

    public sealed record TestMapMatchItem(
        string RelativePath,
        int Score,
        string[] Reasons,
        TestLineMatchItem[] LineMatches,
        int TruncatedLineMatches);

    public sealed record TestLineMatchItem(
        int LineNumber,
        string Text);
}
