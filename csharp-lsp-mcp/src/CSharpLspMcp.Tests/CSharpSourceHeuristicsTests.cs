using System.Reflection;
using CSharpLspMcp.Analysis.Lsp;
using CSharpLspMcp.Lsp;
using Xunit;
using Range = CSharpLspMcp.Lsp.Range;

namespace CSharpLspMcp.Tests;

public class CSharpSourceHeuristicsTests
{
    [Fact]
    public void MergeTopLevelProgramSymbols_AddsProgramInvocationChildren()
    {
        var heuristicsType = typeof(CSharpDocumentAnalysisService).Assembly.GetType("CSharpLspMcp.Analysis.Lsp.CSharpSourceHeuristics");
        Assert.NotNull(heuristicsType);

        var method = heuristicsType!.GetMethod("MergeTopLevelProgramSymbols", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var existing = new[]
        {
            new CSharpDocumentAnalysisService.DocumentSymbolItem(
                "Program.cs",
                "File",
                null,
                null,
                1,
                1,
                Array.Empty<CSharpDocumentAnalysisService.DocumentSymbolItem>())
        };
        var content =
            """
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddAuth();
            var app = builder.Build();
            app.UseAuthentication();
            app.MapApiEndpoints();
            """;

        var result = (CSharpDocumentAnalysisService.DocumentSymbolItem[])method.Invoke(
            null,
            ["/tmp/Program.cs", content, existing])!;

        var programFile = Assert.Single(result);
        Assert.Contains(programFile.Children, child => child.Name == "AddAuth");
        Assert.Contains(programFile.Children, child => child.Name == "UseAuthentication");
        Assert.Contains(programFile.Children, child => child.Name == "MapApiEndpoints");
    }

    [Fact]
    public void ExtractInvocationProbes_FindsCallsInsideMethodBody()
    {
        var heuristicsType = typeof(CSharpDocumentAnalysisService).Assembly.GetType("CSharpLspMcp.Analysis.Lsp.CSharpSourceHeuristics");
        Assert.NotNull(heuristicsType);

        var method = heuristicsType!.GetMethod("ExtractInvocationProbes", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var lines = new[]
        {
            "public async Task Consume(ConsumeContext<FetchItemDataTask> context)",
            "{",
            "    var itemResponse = await _vintedRepository.GetItemById(task.ItemVintedId);",
            "    await _itemBuilder.BuildAndInsertItem(itemResponse, task);",
            "}"
        };
        var range = new Range
        {
            Start = new CSharpLspMcp.Lsp.Position { Line = 0, Character = 0 },
            End = new CSharpLspMcp.Lsp.Position { Line = 4, Character = 1 }
        };

        var probes = ((System.Collections.IEnumerable)method.Invoke(null, [lines, range, "Consume"])!)
            .Cast<object>()
            .Select(probe => (string)probe.GetType().GetProperty("Name")!.GetValue(probe)!)
            .ToArray();

        Assert.Contains("GetItemById", probes);
        Assert.Contains("BuildAndInsertItem", probes);
        Assert.DoesNotContain("Consume", probes);
    }

    [Fact]
    public void FindContainingSymbolRange_PrefersMostSpecificContainingSymbol()
    {
        var heuristicsType = typeof(CSharpDocumentAnalysisService).Assembly.GetType("CSharpLspMcp.Analysis.Lsp.CSharpSourceHeuristics");
        Assert.NotNull(heuristicsType);

        var method = heuristicsType!.GetMethod("FindContainingSymbolRange", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var symbols = new[]
        {
            new DocumentSymbol
            {
                Name = "ItemFetcherService",
                Kind = SymbolKind.Class,
                Range = CreateRange(5, 0, 40, 1),
                SelectionRange = CreateRange(7, 13, 7, 31),
                Children =
                [
                    new DocumentSymbol
                    {
                        Name = "Consume",
                        Kind = SymbolKind.Method,
                        Range = CreateRange(21, 4, 36, 5),
                        SelectionRange = CreateRange(21, 22, 21, 29)
                    }
                ]
            }
        };

        var result = (Range?)method.Invoke(null, [symbols, 26, 20]);

        Assert.NotNull(result);
        Assert.Equal(21, result!.Start.Line);
        Assert.Equal(36, result.End.Line);
    }

    [Theory]
    [InlineData("MapApiEndpoints(this IEndpointRouteBuilder)", "MapApiEndpoints")]
    [InlineData("IEndpointRouteBuilder EndpointExtensions.MapApiEndpoints(IEndpointRouteBuilder app)", "MapApiEndpoints")]
    [InlineData("Consume", "Consume")]
    public void GetInvocationAnchorName_NormalizesMethodDisplayNames(string symbolName, string expected)
    {
        var heuristicsType = typeof(CSharpDocumentAnalysisService).Assembly.GetType("CSharpLspMcp.Analysis.Lsp.CSharpSourceHeuristics");
        Assert.NotNull(heuristicsType);

        var method = heuristicsType!.GetMethod("GetInvocationAnchorName", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var result = (string)method.Invoke(null, [symbolName])!;

        Assert.Equal(expected, result);
    }

    private static Range CreateRange(int startLine, int startCharacter, int endLine, int endCharacter)
        => new()
        {
            Start = new Position { Line = startLine, Character = startCharacter },
            End = new Position { Line = endLine, Character = endCharacter }
        };
}
