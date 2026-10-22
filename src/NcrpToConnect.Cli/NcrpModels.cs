namespace NcrpToConnect.Cli;

internal sealed record NcrpProject(
    string SourcePath,
    string RawXml,
    string? ConnectorId,
    string? ConnectorName,
    string? ConnectorDescription,
    string? BasePath,
    IReadOnlyList<NcrpAuthorizationScheme> AuthorizationSchemes,
    IReadOnlyList<NcrpCategory> Categories,
    IReadOnlyList<NcrpContainer> Containers)
{
    public IReadOnlyList<NcrpContainer> AllContainers => FlattenContainers(Containers).ToArray();

    public IReadOnlyList<NcrpOperation> Operations => FlattenOperations(Containers).ToArray();

    public NcrpProjectExport ToExport() => new(
        SourcePath,
        ConnectorId,
        ConnectorName,
        ConnectorDescription,
        BasePath,
        AuthorizationSchemes,
        Categories,
        Containers);

    private static IEnumerable<NcrpOperation> FlattenOperations(IEnumerable<NcrpContainer> containers)
    {
        foreach (var container in containers)
        {
            foreach (var operation in container.Operations)
            {
                yield return operation;
            }

            foreach (var operation in FlattenOperations(container.Children))
            {
                yield return operation;
            }
        }
    }

    private static IEnumerable<NcrpContainer> FlattenContainers(IEnumerable<NcrpContainer> containers)
    {
        foreach (var container in containers)
        {
            yield return container;

            foreach (var child in FlattenContainers(container.Children))
            {
                yield return child;
            }
        }
    }
}

internal sealed record NcrpProjectExport(
    string SourcePath,
    string? ConnectorId,
    string? ConnectorName,
    string? ConnectorDescription,
    string? BasePath,
    IReadOnlyList<NcrpAuthorizationScheme> AuthorizationSchemes,
    IReadOnlyList<NcrpCategory> Categories,
    IReadOnlyList<NcrpContainer> Containers);

internal sealed record NcrpAuthorizationScheme(
    string? Name,
    string? Description,
    string? Kind,
    string? ApiKeyName,
    string? Location);

internal sealed record NcrpCategory(
    string? Id,
    string? Name,
    string? ParentRef);

internal sealed record NcrpContainer(
    string? Id,
    string? Name,
    string? Description,
    string? ParentRef,
    IReadOnlyList<NcrpContainer> Children,
    IReadOnlyList<NcrpOperation> Operations);

internal sealed record NcrpOperation(
    string? Id,
    string? DisplayName,
    string? Description,
    string? HttpMethod,
    string? Resource,
    string? ContentType,
    bool IsAbsolutePath,
    bool CanBeRepeated,
    bool IsInternal,
    IReadOnlyList<NcrpParameter> InputParameters,
    IReadOnlyList<NcrpParameter> OutputParameters);

internal sealed record NcrpParameter(
    string? Name,
    string? DisplayName,
    string? Description,
    string? Kind,
    string? ParameterType,
    bool IsMandatory,
    bool IsArray,
    string? DefaultValue,
    string? ClassName,
    IReadOnlyList<NcrpParameter>? Members = null,
    int? HttpStatusCode = null)
{
    public IReadOnlyList<NcrpParameter> EffectiveMembers => Members ?? [];

    public string EffectiveName =>
        !string.IsNullOrWhiteSpace(Name) ? Name :
        !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName :
        "field";
}
