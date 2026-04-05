using System.Text.Json;
using CSharpLspMcp.Contracts.Common;
using CSharpLspMcp.Formatting;
using Xunit;

namespace CSharpLspMcp.Tests;

public sealed class StructuredToolOutputFormatterTests
{
    [Fact]
    public void FormatSuccess_SerializesStableEnvelopeForStructuredFormat()
    {
        var output = StructuredToolOutputFormatter.FormatSuccess(
            "csharp_project_overview",
            "structured",
            new FakeStructuredResult("Found 2 projects.", 2));

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;

        Assert.Equal("csharp_project_overview", root.GetProperty("tool").GetString());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("Found 2 projects.", root.GetProperty("summary").GetString());
        Assert.Equal(2, root.GetProperty("data").GetProperty("count").GetInt32());
    }

    [Fact]
    public void FormatSuccess_ReturnsSummaryForSummaryFormat()
    {
        var output = StructuredToolOutputFormatter.FormatSuccess(
            "csharp_project_overview",
            "summary",
            new FakeStructuredResult("Found 2 projects.", 2));

        Assert.Equal("Found 2 projects.", output);
    }

    private sealed record FakeStructuredResult(
        string Summary,
        int Count) : IStructuredToolResult;
}
