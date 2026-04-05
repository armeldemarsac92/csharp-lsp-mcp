using System.Reflection;
using CSharpLspMcp.Analysis.Lsp;
using CSharpLspMcp.Lsp;
using Xunit;
using Range = CSharpLspMcp.Lsp.Range;

namespace CSharpLspMcp.Tests;

public class CSharpWorkspaceAnalysisServiceTests
{
    [Fact]
    public void ShouldIncludeDocument_ExcludesGeneratedAndTestFilesWhenDisabled()
    {
        var method = typeof(CSharpWorkspaceAnalysisService).GetMethod(
            "ShouldIncludeDocument",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var generatedResult = (bool)method.Invoke(null, ["/tmp/src/obj/Debug/net8.0/Foo.g.cs", false, true, Array.Empty<string>()])!;
        var testResult = (bool)method.Invoke(null, ["/tmp/src/App.Tests/FooServiceTests.cs", false, false, Array.Empty<string>()])!;

        Assert.False(generatedResult);
        Assert.False(testResult);
    }

    [Fact]
    public void ShouldIncludeDocument_RespectsExplicitExcludePathPatterns()
    {
        var method = typeof(CSharpWorkspaceAnalysisService).GetMethod(
            "ShouldIncludeDocument",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var result = (bool)method.Invoke(null, ["/tmp/src/App/Contracts/GeneratedDto.cs", true, true, new[] { "Contracts" }])!;

        Assert.False(result);
    }

    [Fact]
    public void FilterDiagnostics_AppliesMinimumSeverityThreshold()
    {
        var method = typeof(CSharpWorkspaceAnalysisService).GetMethod(
            "FilterDiagnostics",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var diagnostics = new[]
        {
            CreateDiagnostic(DiagnosticSeverity.Error, "Error"),
            CreateDiagnostic(DiagnosticSeverity.Warning, "Warning"),
            CreateDiagnostic(DiagnosticSeverity.Information, "Info")
        };

        var result = (Diagnostic[])method.Invoke(null, [diagnostics, "WARNING", Array.Empty<string>(), Array.Empty<string>()])!;

        Assert.Collection(
            result,
            diagnostic => Assert.Equal("Error", diagnostic.Message),
            diagnostic => Assert.Equal("Warning", diagnostic.Message));
    }

    [Fact]
    public void FilterDiagnostics_ExcludesConfiguredDiagnosticCodesAndSources()
    {
        var method = typeof(CSharpWorkspaceAnalysisService).GetMethod(
            "FilterDiagnostics",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var diagnostics = new[]
        {
            CreateDiagnostic(DiagnosticSeverity.Warning, "Keep", code: "CS8602", source: "csharp"),
            CreateDiagnostic(DiagnosticSeverity.Warning, "DropByCode", code: "IDE0005", source: "csharp"),
            CreateDiagnostic(DiagnosticSeverity.Warning, "DropBySource", code: "MB0001", source: "Style")
        };

        var result = (Diagnostic[])method.Invoke(null, [diagnostics, "WARNING", new[] { "IDE0005" }, new[] { "Style" }])!;

        var remaining = Assert.Single(result);
        Assert.Equal("Keep", remaining.Message);
    }

    private static Diagnostic CreateDiagnostic(
        DiagnosticSeverity severity,
        string message,
        string? code = null,
        string? source = null)
        => new()
        {
            Severity = severity,
            Message = message,
            Code = code,
            Source = source,
            Range = new Range
            {
                Start = new Position { Line = 0, Character = 0 },
                End = new Position { Line = 0, Character = 1 }
            }
        };
}
