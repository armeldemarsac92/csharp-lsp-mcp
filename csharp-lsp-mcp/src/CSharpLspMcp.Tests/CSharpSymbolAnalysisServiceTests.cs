using System.Reflection;
using CSharpLspMcp.Analysis.Lsp;
using CSharpLspMcp.Lsp;
using Xunit;
using Range = CSharpLspMcp.Lsp.Range;

namespace CSharpLspMcp.Tests;

public class CSharpSymbolAnalysisServiceTests
{
    [Fact]
    public void SelectBestWorkspaceSymbolMatch_PrefersExactQualifiedName()
    {
        var method = typeof(CSharpSymbolAnalysisService).GetMethod(
            "SelectBestWorkspaceSymbolMatch",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var symbols = new[]
        {
            CreateSymbol(
                "AppUserPrincipalFactory",
                "MeshBoard.Contracts.Authentication",
                "/tmp/auth/AppUserPrincipalFactory.cs",
                5),
            CreateSymbol(
                "AppUserPrincipalFactory",
                "MeshBoard.Legacy.Authentication",
                "/tmp/legacy/AppUserPrincipalFactory.cs",
                8),
            CreateSymbol(
                "AppUserPrincipalFactoryExtensions",
                "MeshBoard.Contracts.Authentication",
                "/tmp/auth/AppUserPrincipalFactoryExtensions.cs",
                3)
        };

        var result = (SymbolInformation)method.Invoke(
            null,
            new object[] { symbols, "MeshBoard.Contracts.Authentication.AppUserPrincipalFactory" })!;

        Assert.Equal("AppUserPrincipalFactory", result.Name);
        Assert.Equal("MeshBoard.Contracts.Authentication", result.ContainerName);
    }

    [Fact]
    public void TryFindDeepestDocumentSymbol_ReturnsMostSpecificChild()
    {
        var method = typeof(CSharpSymbolAnalysisService).GetMethod(
            "TryFindDeepestDocumentSymbol",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var symbols = new[]
        {
            new DocumentSymbol
            {
                Name = "AppUserPrincipalFactory",
                Kind = SymbolKind.Class,
                Range = CreateRange(2, 0, 20, 1),
                SelectionRange = CreateRange(2, 13, 2, 37),
                Children =
                [
                    new DocumentSymbol
                    {
                        Name = "CreatePrincipal",
                        Kind = SymbolKind.Method,
                        Detail = "(AppUser user, string authenticationType)",
                        Range = CreateRange(6, 4, 12, 5),
                        SelectionRange = CreateRange(6, 28, 6, 43),
                        Children = null
                    }
                ]
            }
        };

        var result = method.Invoke(null, new object?[] { symbols, 6, 34, null });

        Assert.NotNull(result);
        Assert.Equal("CreatePrincipal", result!.GetType().GetProperty("Name")?.GetValue(result));
        Assert.Equal("AppUserPrincipalFactory", result.GetType().GetProperty("ContainerName")?.GetValue(result));
    }

    [Fact]
    public void TryFindDeepestDocumentSymbol_PrefersSmallestFlatSymbolOverFileAndNamespace()
    {
        var method = typeof(CSharpSymbolAnalysisService).GetMethod(
            "TryFindDeepestDocumentSymbol",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var symbols = new[]
        {
            new DocumentSymbol
            {
                Name = "AppUserPrincipalFactory.cs",
                Kind = SymbolKind.File,
                Range = CreateRange(0, 0, 20, 0),
                SelectionRange = CreateRange(0, 0, 0, 1)
            },
            new DocumentSymbol
            {
                Name = "Authentication",
                Kind = SymbolKind.Namespace,
                Detail = "MeshBoard.Contracts.Authentication",
                Range = CreateRange(2, 0, 20, 0),
                SelectionRange = CreateRange(2, 10, 2, 24)
            },
            new DocumentSymbol
            {
                Name = "AppUserPrincipalFactory",
                Kind = SymbolKind.Class,
                Range = CreateRange(4, 0, 20, 0),
                SelectionRange = CreateRange(4, 20, 4, 43)
            },
            new DocumentSymbol
            {
                Name = "CreatePrincipal(AppUser user, string authenticationType)",
                Kind = SymbolKind.Method,
                Detail = "ClaimsPrincipal AppUserPrincipalFactory.CreatePrincipal(AppUser user, string authenticationType)",
                Range = CreateRange(6, 4, 20, 0),
                SelectionRange = CreateRange(6, 34, 6, 49)
            }
        };

        var result = method.Invoke(null, new object?[] { symbols, 6, 34, null });

        Assert.NotNull(result);
        Assert.Equal(
            "CreatePrincipal(AppUser user, string authenticationType)",
            result!.GetType().GetProperty("Name")?.GetValue(result));
    }

    [Theory]
    [InlineData("/tmp/tests/AppUserPrincipalFactoryTests.cs", true)]
    [InlineData("/tmp/src/MeshBoard.Contracts/Authentication/AppUserPrincipalFactory.cs", false)]
    public void IsTestPath_DetectsCommonTestLocations(string filePath, bool expected)
    {
        var method = typeof(CSharpSymbolAnalysisService).GetMethod(
            "IsTestPath",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var result = (bool)method.Invoke(null, new object[] { filePath })!;

        Assert.Equal(expected, result);
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
