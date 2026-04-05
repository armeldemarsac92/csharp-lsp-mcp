using System.Text.RegularExpressions;
using CSharpLspMcp.Lsp;

namespace CSharpLspMcp.Analysis.Lsp;

internal static class CSharpSourceHeuristics
{
    private static readonly Regex InvocationRegex = new(
        @"(?:(?<target>[A-Za-z_][A-Za-z0-9_\.]*)\s*\.\s*)?(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex TopLevelProgramInvocationRegex = new(
        @"\b(?<target>app|builder(?:\.Services|\.Host)?|services|host)\.(?<name>(?:Use|Map|Add)[A-Z][A-Za-z0-9_]*)\s*\(",
        RegexOptions.Compiled);
    private static readonly HashSet<string> InvocationKeywords = new(StringComparer.Ordinal)
    {
        "base",
        "catch",
        "default",
        "else",
        "for",
        "foreach",
        "if",
        "lock",
        "nameof",
        "new",
        "return",
        "sizeof",
        "switch",
        "typeof",
        "using",
        "while"
    };

    public static CSharpDocumentAnalysisService.DocumentSymbolItem[] MergeTopLevelProgramSymbols(
        string filePath,
        string? content,
        CSharpDocumentAnalysisService.DocumentSymbolItem[] existingSymbols)
    {
        if (!string.Equals(Path.GetFileName(filePath), "Program.cs", StringComparison.OrdinalIgnoreCase))
            return existingSymbols;

        var topLevelSymbols = ExtractTopLevelProgramSymbols(filePath, content);
        if (topLevelSymbols.Length == 0)
            return existingSymbols;

        if (existingSymbols.Length == 0)
            return topLevelSymbols;

        var fileSymbolIndex = Array.FindIndex(
            existingSymbols,
            symbol => string.Equals(symbol.Kind, nameof(SymbolKind.File), StringComparison.Ordinal));
        if (fileSymbolIndex >= 0)
        {
            var mergedSymbols = existingSymbols.ToArray();
            var fileSymbol = mergedSymbols[fileSymbolIndex];
            mergedSymbols[fileSymbolIndex] = fileSymbol with
            {
                Children = MergeTopLevelProgramChildren(fileSymbol.Children, topLevelSymbols)
            };

            return mergedSymbols;
        }

        if (existingSymbols.Any(symbol => string.Equals(symbol.Name, "Program", StringComparison.Ordinal)))
        {
            return
            [
                new CSharpDocumentAnalysisService.DocumentSymbolItem(
                    "Program.cs",
                    nameof(SymbolKind.File),
                    null,
                    null,
                    1,
                    1,
                    topLevelSymbols),
                .. existingSymbols
            ];
        }

        return existingSymbols;
    }

    public static async Task<HeuristicOutgoingCall[]> ResolveOutgoingCallsAsync(
        LspClient lspClient,
        string filePath,
        string? content,
        CSharpLspMcp.Lsp.Range containingRange,
        string currentSymbolName,
        CancellationToken cancellationToken)
    {
        var source = ReadSource(filePath, content);
        if (string.IsNullOrWhiteSpace(source))
            return Array.Empty<HeuristicOutgoingCall>();

        var lines = SplitLines(source);
        var probes = ExtractInvocationProbes(lines, containingRange, currentSymbolName)
            .Take(24)
            .ToArray();
        if (probes.Length == 0)
            return Array.Empty<HeuristicOutgoingCall>();

        var outgoingCalls = new List<HeuristicOutgoingCall>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var probe in probes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Location[]? definitions;
            try
            {
                definitions = await lspClient.GetDefinitionAsync(
                    filePath,
                    probe.Line,
                    probe.Character,
                    cancellationToken);
            }
            catch (InvalidOperationException)
            {
                break;
            }

            if (definitions == null || definitions.Length == 0)
                continue;

            foreach (var definition in definitions)
            {
                var definitionPath = new Uri(definition.Uri).LocalPath;
                var key = $"{probe.Name}|{definitionPath}|{definition.Range.Start.Line}|{definition.Range.Start.Character}";
                if (!seen.Add(key))
                    continue;

                outgoingCalls.Add(new HeuristicOutgoingCall(
                    probe.Name,
                    "Method",
                    "Resolved by definition fallback.",
                    definitionPath,
                    definition.Range.Start.Line + 1,
                    definition.Range.Start.Character + 1));
            }
        }

        return outgoingCalls.ToArray();
    }

    public static CSharpLspMcp.Lsp.Range? FindContainingSymbolRange(
        object? symbols,
        int line,
        int character)
    {
        return symbols switch
        {
            DocumentSymbol[] documentSymbols => FindDeepestContainingDocumentSymbolRange(documentSymbols, line, character),
            SymbolInformation[] symbolInformation => symbolInformation
                .Where(symbol => ContainsPosition(symbol.Location.Range, line, character))
                .OrderBy(symbol => GetRangeSpan(symbol.Location.Range))
                .Select(symbol => symbol.Location.Range)
                .FirstOrDefault(),
            _ => null
        };
    }

    public static string GetInvocationAnchorName(string symbolName)
    {
        if (string.IsNullOrWhiteSpace(symbolName))
            return symbolName;

        var normalized = symbolName.Trim();
        var parameterIndex = normalized.IndexOf('(');
        if (parameterIndex >= 0)
            normalized = normalized[..parameterIndex];

        var dottedMemberIndex = normalized.LastIndexOf('.');
        if (dottedMemberIndex >= 0 && dottedMemberIndex < normalized.Length - 1)
            normalized = normalized[(dottedMemberIndex + 1)..];

        var lastSpaceIndex = normalized.LastIndexOf(' ');
        if (lastSpaceIndex >= 0 && lastSpaceIndex < normalized.Length - 1)
            normalized = normalized[(lastSpaceIndex + 1)..];

        return normalized.Trim();
    }

    private static CSharpDocumentAnalysisService.DocumentSymbolItem[] ExtractTopLevelProgramSymbols(
        string filePath,
        string? content)
    {
        var source = ReadSource(filePath, content);
        if (string.IsNullOrWhiteSpace(source))
            return Array.Empty<CSharpDocumentAnalysisService.DocumentSymbolItem>();

        var symbols = new List<CSharpDocumentAnalysisService.DocumentSymbolItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var lines = SplitLines(source);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = TrimLineComment(lines[index]);
            foreach (Match match in TopLevelProgramInvocationRegex.Matches(line))
            {
                var methodName = match.Groups["name"].Value;
                var key = $"{methodName}|{index + 1}";
                if (!seen.Add(key))
                    continue;

                symbols.Add(new CSharpDocumentAnalysisService.DocumentSymbolItem(
                    methodName,
                    nameof(SymbolKind.Method),
                    line.Trim(),
                    null,
                    index + 1,
                    match.Groups["name"].Index + 1,
                    Array.Empty<CSharpDocumentAnalysisService.DocumentSymbolItem>()));
            }
        }

        return symbols.ToArray();
    }

    private static CSharpDocumentAnalysisService.DocumentSymbolItem[] MergeTopLevelProgramChildren(
        CSharpDocumentAnalysisService.DocumentSymbolItem[] existingChildren,
        CSharpDocumentAnalysisService.DocumentSymbolItem[] topLevelSymbols)
    {
        if (existingChildren.Length == 0)
            return topLevelSymbols;

        return existingChildren
            .Concat(topLevelSymbols)
            .DistinctBy(symbol => $"{symbol.Kind}|{symbol.Name}|{symbol.Line}|{symbol.Character}")
            .OrderBy(symbol => symbol.Line)
            .ThenBy(symbol => symbol.Character)
            .ToArray();
    }

    private static CSharpLspMcp.Lsp.Range? FindDeepestContainingDocumentSymbolRange(
        IEnumerable<DocumentSymbol> symbols,
        int line,
        int character)
    {
        CSharpLspMcp.Lsp.Range? bestMatch = null;
        foreach (var symbol in symbols)
        {
            if (!ContainsPosition(symbol.Range, line, character))
                continue;

            var childMatch = symbol.Children == null
                ? null
                : FindDeepestContainingDocumentSymbolRange(symbol.Children, line, character);
            var candidate = childMatch ?? symbol.Range;
            bestMatch = SelectMoreSpecificRange(bestMatch, candidate);
        }

        return bestMatch;
    }

    private static IEnumerable<InvocationProbe> ExtractInvocationProbes(
        string[] lines,
        CSharpLspMcp.Lsp.Range containingRange,
        string currentSymbolName)
    {
        if (lines.Length == 0)
            yield break;

        var startLine = Math.Clamp(containingRange.Start.Line, 0, lines.Length - 1);
        var endLine = Math.Clamp(containingRange.End.Line, startLine, lines.Length - 1);
        var bodyStarted = false;

        for (var lineIndex = startLine; lineIndex <= endLine; lineIndex++)
        {
            var originalLine = lines[lineIndex];
            var startCharacter = lineIndex == startLine
                ? Math.Clamp(containingRange.Start.Character, 0, originalLine.Length)
                : 0;
            var endCharacter = lineIndex == endLine
                ? Math.Clamp(containingRange.End.Character, 0, originalLine.Length)
                : originalLine.Length;

            if (startCharacter >= endCharacter)
                continue;

            var lineSegment = TrimLineComment(originalLine[startCharacter..endCharacter]);
            if (string.IsNullOrWhiteSpace(lineSegment))
                continue;

            var scanStart = 0;
            if (!bodyStarted)
            {
                var braceIndex = lineSegment.IndexOf('{');
                var arrowIndex = lineSegment.IndexOf("=>", StringComparison.Ordinal);
                if (braceIndex < 0 && arrowIndex < 0)
                    continue;

                if (braceIndex >= 0 && (arrowIndex < 0 || braceIndex < arrowIndex))
                {
                    scanStart = braceIndex + 1;
                }
                else
                {
                    scanStart = arrowIndex + 2;
                }

                bodyStarted = true;
            }

            foreach (Match match in InvocationRegex.Matches(lineSegment, scanStart))
            {
                var methodName = match.Groups["name"].Value;
                if (!IsInvocationCandidate(lineSegment, match, methodName, currentSymbolName))
                    continue;

                yield return new InvocationProbe(
                    methodName,
                    lineIndex,
                    startCharacter + match.Groups["name"].Index);
            }
        }
    }

    private static bool IsInvocationCandidate(
        string lineSegment,
        Match match,
        string methodName,
        string currentSymbolName)
    {
        if (InvocationKeywords.Contains(methodName))
            return false;

        if (string.Equals(methodName, currentSymbolName, StringComparison.Ordinal))
            return false;

        var prefix = lineSegment[..match.Index].TrimEnd();
        if (prefix.EndsWith("new", StringComparison.Ordinal) ||
            prefix.EndsWith("class", StringComparison.Ordinal) ||
            prefix.EndsWith("record", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static bool ContainsPosition(CSharpLspMcp.Lsp.Range range, int line, int character)
    {
        if (line < range.Start.Line || line > range.End.Line)
            return false;

        if (line == range.Start.Line && character < range.Start.Character)
            return false;

        if (line == range.End.Line && character > range.End.Character)
            return false;

        return true;
    }

    private static int GetRangeSpan(CSharpLspMcp.Lsp.Range range)
        => ((range.End.Line - range.Start.Line) * 10_000) + (range.End.Character - range.Start.Character);

    private static CSharpLspMcp.Lsp.Range SelectMoreSpecificRange(
        CSharpLspMcp.Lsp.Range? current,
        CSharpLspMcp.Lsp.Range candidate)
    {
        if (current == null)
            return candidate;

        return GetRangeSpan(candidate) < GetRangeSpan(current)
            ? candidate
            : current;
    }

    private static string ReadSource(string filePath, string? content)
        => string.IsNullOrEmpty(content) ? File.ReadAllText(filePath) : content;

    private static string[] SplitLines(string content)
        => content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static string TrimLineComment(string line)
    {
        var commentIndex = line.IndexOf("//", StringComparison.Ordinal);
        return commentIndex >= 0 ? line[..commentIndex] : line;
    }

    public sealed record HeuristicOutgoingCall(
        string Name,
        string Kind,
        string? Detail,
        string FilePath,
        int Line,
        int Character);

    private sealed record InvocationProbe(
        string Name,
        int Line,
        int Character);
}
