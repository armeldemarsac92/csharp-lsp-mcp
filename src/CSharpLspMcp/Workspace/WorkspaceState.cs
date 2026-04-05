namespace CSharpLspMcp.Workspace;

public sealed class WorkspaceState
{
    public string? CurrentPath { get; private set; }

    public void SetPath(string? path)
    {
        CurrentPath = path;
    }
}
