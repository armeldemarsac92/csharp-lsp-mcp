using System.Reflection;
using CSharpLspMcp.Lsp;
using CSharpLspMcp.Workspace;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CSharpLspMcp.Tests;

public class CSharpWorkspaceSessionTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("class C { }", false)]
    public void ShouldReadContentFromDisk_TreatsEmptyStringAsMissingContent(string? content, bool expected)
    {
        var method = typeof(CSharpWorkspaceSession).GetMethod(
            "ShouldReadContentFromDisk",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var result = (bool)method.Invoke(null, new object?[] { content })!;
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetAbsolutePath_UsesWorkspacePathForRelativePath()
    {
        var session = CreateSession("/tmp/workspace-root");

        var path = session.GetAbsolutePath("src/Program.cs");

        Assert.Equal("/tmp/workspace-root/src/Program.cs", path);
    }

    [Fact]
    public void GetAbsolutePath_ReturnsAbsolutePathUnchanged()
    {
        var session = CreateSession("/tmp/workspace-root");

        var path = session.GetAbsolutePath("/tmp/other/Program.cs");

        Assert.Equal("/tmp/other/Program.cs", path);
    }

    private static CSharpWorkspaceSession CreateSession(string? workspacePath)
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var solutionFilter = new SolutionFilter(loggerFactory.CreateLogger<SolutionFilter>());
        var lspClient = new LspClient(loggerFactory.CreateLogger<LspClient>(), solutionFilter);
        var workspaceState = new WorkspaceState();
        workspaceState.SetPath(workspacePath);

        return new CSharpWorkspaceSession(
            loggerFactory.CreateLogger<CSharpWorkspaceSession>(),
            lspClient,
            workspaceState);
    }
}
