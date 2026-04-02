using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CSharpLspMcp.Lsp;
using Xunit;

namespace CSharpLspMcp.Tests;

public class LspClientTests
{
    [Fact]
    public async Task ReadContentLengthAsync_ReadsLengthAndPayload()
    {
        var payload = Encoding.UTF8.GetBytes("{\"a\":\"✓\"}");
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        var stream = new MemoryStream(header.Concat(payload).ToArray());

        var readLength = await InvokeReadContentLengthAsync(stream);
        Assert.Equal(payload.Length, readLength);

        var readBuffer = new byte[payload.Length];
        var readOk = await InvokeReadExactAsync(stream, readBuffer, payload.Length);
        Assert.True(readOk);
        Assert.Equal(payload, readBuffer);
    }

    [Fact]
    public async Task ReadExactAsync_ReturnsFalseOnShortStream()
    {
        var stream = new MemoryStream(new byte[] { 1, 2 });
        var readBuffer = new byte[3];

        var readOk = await InvokeReadExactAsync(stream, readBuffer, readBuffer.Length);

        Assert.False(readOk);
    }

    [Fact]
    public void CreateStartInfo_ConfiguresWorkingDirectoryAndSolutionArgument()
    {
        var startInfo = InvokeCreateStartInfo(
            "csharp-ls",
            "/tmp/workspace",
            "/tmp/workspace/MeshBoard.slnx");

        Assert.Equal("csharp-ls", startInfo.FileName);
        Assert.Equal("/tmp/workspace", startInfo.WorkingDirectory);
        Assert.Equal(new[] { "--solution", "MeshBoard.slnx" }, startInfo.ArgumentList);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
    }

    [Fact]
    public void CreateStartInfo_OmitsSolutionArgumentWhenSolutionPathIsMissing()
    {
        var startInfo = InvokeCreateStartInfo("csharp-ls", "/tmp/workspace", null);

        Assert.Equal("csharp-ls", startInfo.FileName);
        Assert.Equal("/tmp/workspace", startInfo.WorkingDirectory);
        Assert.Empty(startInfo.ArgumentList);
    }

    [Fact]
    public void JsonRpcSuccessResponse_SerializesNullResult()
    {
        var json = JsonSerializer.Serialize(
            new JsonRpcSuccessResponse
            {
                Id = 2,
                Result = null
            },
            CreateClientJsonOptions());

        Assert.Contains("\"id\":2", json);
        Assert.Contains("\"result\":null", json);
        Assert.DoesNotContain("\"error\":", json);
    }

    [Fact]
    public void CreateWorkspaceConfigurationResult_ReturnsOneEmptyObjectPerItem()
    {
        using var doc = JsonDocument.Parse("""{"items":[{"section":"csharp"},{"section":"other"}]}""");

        var result = InvokeCreateWorkspaceConfigurationResult(doc.RootElement.Clone());

        Assert.Equal(2, result.Length);
        Assert.All(result, item =>
        {
            var dictionary = Assert.IsAssignableFrom<IDictionary<string, object?>>(item);
            Assert.Empty(dictionary);
        });
    }

    [Fact]
    public void TryGetRequestId_ParsesNumericAndStringIds()
    {
        using var numberDoc = JsonDocument.Parse("42");
        using var stringDoc = JsonDocument.Parse("\"42\"");
        using var invalidDoc = JsonDocument.Parse("\"not-a-number\"");

        Assert.True(InvokeTryGetRequestId(numberDoc.RootElement.Clone(), out var numericId));
        Assert.Equal(42, numericId);

        Assert.True(InvokeTryGetRequestId(stringDoc.RootElement.Clone(), out var stringId));
        Assert.Equal(42, stringId);

        Assert.False(InvokeTryGetRequestId(invalidDoc.RootElement.Clone(), out _));
    }

    private static Task<int?> InvokeReadContentLengthAsync(Stream stream)
    {
        var method = typeof(LspClient).GetMethod(
            "ReadContentLengthAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var task = (Task<int?>)method.Invoke(null, new object?[] { stream, CancellationToken.None })!;
        return task;
    }

    private static Task<bool> InvokeReadExactAsync(Stream stream, byte[] buffer, int length)
    {
        var method = typeof(LspClient).GetMethod(
            "ReadExactAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var task = (Task<bool>)method.Invoke(null, new object?[] { stream, buffer, length, CancellationToken.None })!;
        return task;
    }

    private static System.Diagnostics.ProcessStartInfo InvokeCreateStartInfo(
        string lspPath,
        string? workingDirectory,
        string? solutionPath)
    {
        var method = typeof(LspClient).GetMethod(
            "CreateStartInfo",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return (System.Diagnostics.ProcessStartInfo)method.Invoke(
            null,
            new object?[] { lspPath, workingDirectory, solutionPath })!;
    }

    private static object?[] InvokeCreateWorkspaceConfigurationResult(JsonElement @params)
    {
        var method = typeof(LspClient).GetMethod(
            "CreateWorkspaceConfigurationResult",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return (object?[])method.Invoke(null, new object?[] { @params })!;
    }

    private static bool InvokeTryGetRequestId(JsonElement idElement, out int id)
    {
        var method = typeof(LspClient).GetMethod(
            "TryGetRequestId",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var args = new object?[] { idElement, 0 };
        var result = (bool)method.Invoke(null, args)!;
        id = (int)args[1]!;
        return result;
    }

    private static JsonSerializerOptions CreateClientJsonOptions()
        => new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
}
