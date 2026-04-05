namespace CSharpLspMcp.Contracts.Common;

public sealed record SimpleStructuredResult(
    string Summary,
    string? Message = null) : IStructuredToolResult;
