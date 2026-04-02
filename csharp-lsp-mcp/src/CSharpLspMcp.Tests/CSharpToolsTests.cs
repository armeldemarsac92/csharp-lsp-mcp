using System.Reflection;
using CSharpLspMcp.Tools;
using Xunit;

namespace CSharpLspMcp.Tests;

public class CSharpToolsTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("class C { }", false)]
    public void ShouldReadContentFromDisk_TreatsEmptyStringAsMissingContent(string? content, bool expected)
    {
        var method = typeof(CSharpTools).GetMethod(
            "ShouldReadContentFromDisk",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var result = (bool)method.Invoke(null, new object?[] { content })!;
        Assert.Equal(expected, result);
    }
}
