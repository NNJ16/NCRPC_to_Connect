namespace NcrpToConnect.Cli;

internal sealed class ConnectorCatalog
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ConnectorCatalogOperation>> operationsByConnector;
    private readonly IReadOnlyList<ConnectorCatalogOperation> allOperations;

    private ConnectorCatalog(
        IReadOnlyDictionary<string, IReadOnlyList<ConnectorCatalogOperation>> operationsByConnector,
        IReadOnlyList<ConnectorCatalogOperation> allOperations)
    {
        this.operationsByConnector = operationsByConnector;
        this.allOperations = allOperations;
    }

    public int ConnectorCount => operationsByConnector.Count;

    public int OperationCount => allOperations.Count;

    public static ConnectorCatalog LoadFromDirectory(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return Empty();
        }

        var reader = new NcrpProjectReader();
        var operations = new List<ConnectorCatalogOperation>();

        foreach (var file in Directory.EnumerateFiles(directoryPath, "*.ncrcp"))
        {
            NcrpProject project;
            try
            {
                project = reader.Read(file);
            }
            catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException or System.Xml.XmlException)
            {
                continue;
            }

            var aliases = ResolveAliases(project, file);
            foreach (var operation in project.Operations)
            {
                foreach (var alias in aliases)
                {
                    operations.Add(new ConnectorCatalogOperation(alias, project, operation));
                }
            }
        }

        return FromOperations(operations);
    }

    public static ConnectorCatalog FromProjects(IEnumerable<NcrpProject> projects)
    {
        var operations = new List<ConnectorCatalogOperation>();

        foreach (var project in projects)
        {
            var aliases = ResolveAliases(project, project.SourcePath);
            foreach (var operation in project.Operations)
            {
                foreach (var alias in aliases)
                {
                    operations.Add(new ConnectorCatalogOperation(alias, project, operation));
                }
            }
        }

        return FromOperations(operations);
    }

    public ConnectorCatalogOperation? FindOperation(string? connectorName, string? operationIdentifier)
    {
        if (string.IsNullOrWhiteSpace(operationIdentifier))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(connectorName)
            && operationsByConnector.TryGetValue(Normalize(connectorName), out var connectorOperations))
        {
            var operation = FindByOperationName(connectorOperations, operationIdentifier);
            if (operation is not null)
            {
                return operation;
            }
        }

        var matches = allOperations
            .Where(operation => OperationNameMatches(operation.Operation, operationIdentifier))
            .GroupBy(operation => operation.Operation.Id ?? operation.Operation.DisplayName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return matches.Length == 1 ? matches[0].First() : null;
    }

    private static ConnectorCatalog Empty() =>
        new(new Dictionary<string, IReadOnlyList<ConnectorCatalogOperation>>(StringComparer.OrdinalIgnoreCase), []);

    private static ConnectorCatalog FromOperations(IReadOnlyList<ConnectorCatalogOperation> operations)
    {
        var grouped = operations
            .GroupBy(operation => Normalize(operation.ConnectorAlias), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ConnectorCatalogOperation>)group.ToArray(),
                StringComparer.OrdinalIgnoreCase);

        return new ConnectorCatalog(grouped, operations);
    }

    private static ConnectorCatalogOperation? FindByOperationName(IEnumerable<ConnectorCatalogOperation> operations, string operationIdentifier) =>
        operations.FirstOrDefault(operation => OperationNameMatches(operation.Operation, operationIdentifier));

    private static bool OperationNameMatches(NcrpOperation operation, string operationIdentifier) =>
        string.Equals(operation.Id, operationIdentifier, StringComparison.OrdinalIgnoreCase)
        || string.Equals(operation.DisplayName, operationIdentifier, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> ResolveAliases(NcrpProject project, string filePath)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFileNameWithoutExtension(filePath)
        };

        AddAlias(project.ConnectorName);
        AddAlias(project.ConnectorId);

        return aliases;

        void AddAlias(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            aliases.Add(value);
            if (value.EndsWith(".com", StringComparison.OrdinalIgnoreCase))
            {
                aliases.Add(value[..^4]);
            }
        }
    }

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

internal sealed record ConnectorCatalogOperation(
    string ConnectorAlias,
    NcrpProject Project,
    NcrpOperation Operation);
