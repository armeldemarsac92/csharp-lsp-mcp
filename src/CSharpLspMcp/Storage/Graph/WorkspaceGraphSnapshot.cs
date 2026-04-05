namespace CSharpLspMcp.Storage.Graph;

public static class WorkspaceGraphNodeKinds
{
    public const string Solution = "Solution";
    public const string Project = "Project";
    public const string Document = "Document";
    public const string Namespace = "Namespace";
    public const string Type = "Type";
    public const string Method = "Method";
    public const string Property = "Property";
    public const string Field = "Field";
    public const string Event = "Event";
}

public static class WorkspaceGraphEdgeKinds
{
    public const string Contains = "contains";
    public const string DeclaredIn = "declared_in";
    public const string DependsOnProject = "depends_on_project";
    public const string Inherits = "inherits";
    public const string Implements = "implements";
    public const string Overrides = "overrides";
}

public sealed record WorkspaceGraphSnapshot(
    string WorkspaceRoot,
    string WorkspaceTargetPath,
    DateTimeOffset BuiltAtUtc,
    string BuilderVersion,
    string BuildMode,
    int ProjectsIndexed,
    int DocumentsIndexed,
    int SymbolsIndexed,
    int EdgesIndexed,
    WorkspaceGraphCountItem[] NodeCounts,
    WorkspaceGraphCountItem[] EdgeCounts,
    WorkspaceGraphProjectSummary[] Projects,
    WorkspaceGraphNode[] Nodes,
    WorkspaceGraphEdge[] Edges,
    string[] Warnings);

public sealed record WorkspaceGraphCountItem(
    string Kind,
    int Count);

public sealed record WorkspaceGraphProjectSummary(
    string Name,
    string FilePath,
    string AssemblyName,
    string[] TargetFrameworks,
    bool IsTestProject,
    int DocumentsIndexed,
    int SymbolsIndexed,
    int ProjectReferenceCount);

public sealed record WorkspaceGraphNode(
    string Id,
    string Kind,
    string DisplayName,
    string ProjectName,
    string? FilePath,
    int? Line,
    int? Character,
    string? DocumentationId,
    string? MetadataName);

public sealed record WorkspaceGraphEdge(
    string Kind,
    string SourceId,
    string TargetId);
