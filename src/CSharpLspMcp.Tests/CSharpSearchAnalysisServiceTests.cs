using System.Reflection;
using CSharpLspMcp.Analysis.Lsp;
using CSharpLspMcp.Lsp;
using Xunit;
using Range = CSharpLspMcp.Lsp.Range;

namespace CSharpLspMcp.Tests;

public class CSharpSearchAnalysisServiceTests
{
    [Fact]
    public void RankAndFilterSymbols_PrefersQualifiedProductionMatchOverTestFallback()
    {
        var method = typeof(CSharpSearchAnalysisService).GetMethod(
            "RankAndFilterSymbols",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var query = "MeshBoard.Contracts.Authentication.AppUserPrincipalFactory";
        var symbols = new[]
        {
            CreateSymbol(
                "AppUserPrincipalFactory",
                "MeshBoard.Contracts.Authentication",
                "/tmp/src/MeshBoard.Contracts/Authentication/AppUserPrincipalFactory.cs",
                4),
            CreateSymbol(
                "AppUserPrincipalFactoryTests",
                "MeshBoard.UnitTests",
                "/tmp/tests/MeshBoard.UnitTests/AppUserPrincipalFactoryTests.cs",
                5)
        };

        var result = (SymbolInformation[])method.Invoke(null, [query, symbols])!;

        var symbol = Assert.Single(result);
        Assert.Equal("AppUserPrincipalFactory", symbol.Name);
        Assert.Equal("MeshBoard.Contracts.Authentication", symbol.ContainerName);
    }

    [Fact]
    public void RankAndFilterSymbols_UsesPathHeuristicsWhenContainerNameIsMissing()
    {
        var method = typeof(CSharpSearchAnalysisService).GetMethod(
            "RankAndFilterSymbols",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var query = "MeshBoard.Contracts.Authentication.AppUserPrincipalFactory";
        var symbols = new[]
        {
            CreateSymbol(
                "AppUserPrincipalFactory",
                null,
                "/tmp/src/MeshBoard.Contracts/Authentication/AppUserPrincipalFactory.cs",
                4),
            CreateSymbol(
                "AppUserPrincipalFactoryTests",
                null,
                "/tmp/tests/MeshBoard.UnitTests/AppUserPrincipalFactoryTests.cs",
                5)
        };

        var result = (SymbolInformation[])method.Invoke(null, [query, symbols])!;

        var symbol = Assert.Single(result);
        Assert.Equal("AppUserPrincipalFactory", symbol.Name);
    }

    [Fact]
    public void RankAndFilterSymbols_OrdersTestMatchesAfterProductionMatchesForSimpleQueries()
    {
        var method = typeof(CSharpSearchAnalysisService).GetMethod(
            "RankAndFilterSymbols",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var symbols = new[]
        {
            CreateSymbol(
                "TopicDiscoveryService",
                "MeshBoard.Application.Topics",
                "/tmp/src/MeshBoard.Application/Topics/TopicDiscoveryService.cs",
                10),
            CreateSymbol(
                "TopicDiscoveryServiceTests",
                "MeshBoard.UnitTests",
                "/tmp/tests/MeshBoard.UnitTests/TopicDiscoveryServiceTests.cs",
                8)
        };

        var result = (SymbolInformation[])method.Invoke(null, ["TopicDiscoveryService", symbols])!;

        Assert.Equal("TopicDiscoveryService", result[0].Name);
    }

    private static SymbolInformation CreateSymbol(
        string name,
        string? containerName,
        string filePath,
        int line)
        => new()
        {
            Name = name,
            Kind = SymbolKind.Class,
            ContainerName = containerName,
            Location = new Location
            {
                Uri = new Uri(filePath).ToString(),
                Range = CreateRange(line, 0, line, name.Length)
            }
        };

    private static Range CreateRange(int startLine, int startCharacter, int endLine, int endCharacter)
        => new()
        {
            Start = new Position { Line = startLine, Character = startCharacter },
            End = new Position { Line = endLine, Character = endCharacter }
        };
}
