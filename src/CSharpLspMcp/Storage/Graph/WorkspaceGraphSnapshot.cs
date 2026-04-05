namespace CSharpLspMcp.Storage.Graph;

public static class WorkspaceGraphSchema
{
    public const int CurrentVersion = 2;
}

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
    public const string DiRegistration = "DiRegistration";
}

public static class WorkspaceGraphEdgeKinds
{
    public const string Contains = "contains";
    public const string DeclaredIn = "declared_in";
    public const string DependsOnProject = "depends_on_project";
    public const string Inherits = "inherits";
    public const string Implements = "implements";
    public const string Overrides = "overrides";
    public const string Calls = "calls";
    public const string RegisteredAs = "registered_as";
    public const string ConsumedBy = "consumed_by";
}

public sealed record WorkspaceGraphSnapshot(
    int SchemaVersion,
    string WorkspaceRoot,
    string WorkspaceTargetPath,
    DateTimeOffset BuiltAtUtc,
    string BuilderVersion,
    string BuildMode,
    bool IncludeTests,
    bool IncludeGenerated,
    int ProjectsIndexed,
    int DocumentsIndexed,
    int SymbolsIndexed,
    int EdgesIndexed,
    WorkspaceGraphCountItem[] NodeCounts,
    WorkspaceGraphCountItem[] EdgeCounts,
    WorkspaceGraphProjectSummary[] Projects,
    WorkspaceGraphProjectState[] ProjectStates,
    WorkspaceGraphNode[] Nodes,
    WorkspaceGraphEdge[] Edges,
    string[] Features,
    string[] Warnings);

public sealed record WorkspaceGraphCountItem(
    string Kind,
    int Count);

public sealed record WorkspaceGraphProjectSummary(
    string Id,
    string Name,
    string FilePath,
    string AssemblyName,
    string[] TargetFrameworks,
    bool IsTestProject,
    int DocumentsIndexed,
    int SymbolsIndexed,
    int ProjectReferenceCount);

public sealed record WorkspaceGraphProjectState(
    string ProjectId,
    string Name,
    string FilePath,
    string Fingerprint,
    string[] ReferencedProjectIds);

public sealed record WorkspaceGraphNode(
    string Id,
    string Kind,
    string DisplayName,
    string ProjectName,
    string? OwningProjectId,
    string? FilePath,
    int? Line,
    int? Character,
    string? DocumentationId,
    string? MetadataName);

public sealed record WorkspaceGraphEdge(
    string Kind,
    string SourceId,
    string TargetId);
