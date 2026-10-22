using System.Text;
using System.Text.Json;
using NcrpToConnect.Cli;

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = true
};

var options = CliOptions.Parse(args);
if (options.ShowHelp)
{
    PrintUsage();
    return options.HasError ? 1 : 0;
}

try
{
    if (IsPortalExport(options.InputPath!))
    {
        var reader = new PortalExportReader();
        var report = reader.Read(options.InputPath!);

        PrintPortalExportSummary(report);

        if (options.JsonOutputPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.JsonOutputPath)) ?? ".");
            var json = JsonSerializer.Serialize(report, jsonOptions);
            await File.WriteAllTextAsync(options.JsonOutputPath, json);
            Console.WriteLine();
            Console.WriteLine($"Portal page/portlet report JSON written to: {options.JsonOutputPath}");
        }
    }
    else if (IsConnectFlowPackage(options.InputPath!))
    {
        var packageReader = new ConnectFlowPackageReader();
        var package = packageReader.Read(options.InputPath!);
        var connectorCatalog = ConnectorCatalog.LoadFromDirectory(Path.GetDirectoryName(Path.GetFullPath(options.InputPath!)));

        PrintConnectFlowPackageSummary(package, connectorCatalog);

        if (options.XmlOutputPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.XmlOutputPath)) ?? ".");
            await File.WriteAllTextAsync(options.XmlOutputPath, GeneratePackageWorkflowXml(package));
            Console.WriteLine();
            Console.WriteLine($"Embedded WorkflowData XML written to: {options.XmlOutputPath}");
        }

        if (options.JsonOutputPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.JsonOutputPath)) ?? ".");
            var json = JsonSerializer.Serialize(package, jsonOptions);
            await File.WriteAllTextAsync(options.JsonOutputPath, json);
            Console.WriteLine($"Flow package inventory JSON written to: {options.JsonOutputPath}");
        }

        if (options.FlowOutputPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.FlowOutputPath)) ?? ".");
            var flowScript = GeneratePackageDraft(package, connectorCatalog);
            await File.WriteAllTextAsync(options.FlowOutputPath, flowScript);
            Console.WriteLine($"Flow Connect package draft written to: {options.FlowOutputPath}");
        }
    }
    else if (IsConnectFlowExport(options.InputPath!))
    {
        var reader = new ConnectFlowReader();
        var project = reader.Read(options.InputPath!);
        var connectorCatalog = ConnectorCatalog.LoadFromDirectory(Path.GetDirectoryName(Path.GetFullPath(options.InputPath!)));

        PrintConnectFlowSummary(project);

        if (options.XmlOutputPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.XmlOutputPath)) ?? ".");
            await File.WriteAllTextAsync(options.XmlOutputPath, project.RawWorkflowXml);
            Console.WriteLine();
            Console.WriteLine($"Embedded WorkflowData XML written to: {options.XmlOutputPath}");
        }

        if (options.JsonOutputPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.JsonOutputPath)) ?? ".");
            var json = JsonSerializer.Serialize(project, jsonOptions);
            await File.WriteAllTextAsync(options.JsonOutputPath, json);
            Console.WriteLine($"Flow inventory JSON written to: {options.JsonOutputPath}");
        }

        if (options.FlowOutputPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.FlowOutputPath)) ?? ".");
            var flowScript = FlowConnectDraftGenerator.Generate(project, connectorCatalog);
            await File.WriteAllTextAsync(options.FlowOutputPath, flowScript);
            Console.WriteLine($"Flow Connect draft written to: {options.FlowOutputPath}");
        }
    }
    else
    {
        var reader = new NcrpProjectReader();
        var project = reader.Read(options.InputPath!);

        PrintNcrpSummary(project);

        if (options.XmlOutputPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.XmlOutputPath)) ?? ".");
            await File.WriteAllTextAsync(options.XmlOutputPath, project.RawXml);
            Console.WriteLine();
            Console.WriteLine($"Decoded XML written to: {options.XmlOutputPath}");
        }

        if (options.JsonOutputPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.JsonOutputPath)) ?? ".");
            var json = JsonSerializer.Serialize(project.ToExport(), jsonOptions);
            await File.WriteAllTextAsync(options.JsonOutputPath, json);
            Console.WriteLine($"Connector inventory JSON written to: {options.JsonOutputPath}");
        }

        if (options.FlowOutputPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.FlowOutputPath)) ?? ".");
            var flowScript = FlowScriptDraftGenerator.Generate(project);
            await File.WriteAllTextAsync(options.FlowOutputPath, flowScript);
            Console.WriteLine($"Flow Script draft written to: {options.FlowOutputPath}");
        }
    }

    return 0;
}
catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException or System.Xml.XmlException or System.Reflection.TargetInvocationException)
{
    Console.Error.WriteLine($"Error: {ex.InnerException?.Message ?? ex.Message}");
    return 2;
}

static bool IsPortalExport(string inputPath)
{
    var extension = Path.GetExtension(inputPath);
    if (string.Equals(extension, ".ptl", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    if (!string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    try
    {
        using var stream = File.OpenRead(inputPath);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        return root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array
            && root.TryGetProperty("listOfValues", out _);
    }
    catch (JsonException)
    {
        return false;
    }
}

static bool IsConnectFlowExport(string inputPath) =>
    string.Equals(Path.GetExtension(inputPath), ".flow", StringComparison.OrdinalIgnoreCase);

static bool IsConnectFlowPackage(string inputPath) =>
    string.Equals(Path.GetExtension(inputPath), ".wap", StringComparison.OrdinalIgnoreCase);

static void PrintNcrpSummary(NcrpProject project)
{
    Console.WriteLine($"Connector: {project.ConnectorName} ({project.ConnectorId})");
    Console.WriteLine($"Base path:  {ValueOrDash(project.BasePath)}");
    Console.WriteLine($"Containers: {project.AllContainers.Count} total, {project.Containers.Count} top-level");
    Console.WriteLine($"Operations: {project.Operations.Count}");

    if (project.AuthorizationSchemes.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("Authorization:");
        foreach (var scheme in project.AuthorizationSchemes)
        {
            Console.WriteLine($"  - {scheme.Name} [{scheme.Kind}] {ValueOrDash(scheme.Location)} {ValueOrDash(scheme.ApiKeyName)}");
        }
    }

    Console.WriteLine();
    Console.WriteLine("Containers and operations:");
    foreach (var container in project.Containers)
    {
        PrintContainer(container, indent: 0);
    }
}

static void PrintContainer(NcrpContainer container, int indent)
{
    var padding = new string(' ', indent * 2);
    Console.WriteLine($"{padding}- {container.Name} ({container.Id})");

    foreach (var operation in container.Operations)
    {
        var required = operation.InputParameters.Count(p => p.IsMandatory);
        Console.WriteLine($"{padding}  {operation.HttpMethod,-6} {operation.Resource,-45} {operation.Id} ({operation.InputParameters.Count} inputs, {required} required)");
    }

    foreach (var child in container.Children)
    {
        PrintContainer(child, indent + 1);
    }
}

static void PrintConnectFlowPackageSummary(ConnectFlowPackage package, ConnectorCatalog connectorCatalog)
{
    Console.WriteLine($"Flow package: {Path.GetFileName(package.SourcePath)}");
    Console.WriteLine($"Flows:        {package.Entries.Count}");
    Console.WriteLine($"REST calls:   {package.Entries.Sum(entry => entry.Flow.RestSteps.Count)}");
    Console.WriteLine($"DB calls:     {package.Entries.Sum(entry => entry.Flow.DatabaseSteps.Count)}");
    Console.WriteLine($"Catalog:      {connectorCatalog.ConnectorCount} connector alias(es), {connectorCatalog.OperationCount} operation alias(es)");

    Console.WriteLine();
    Console.WriteLine("Flows:");
    foreach (var entry in package.Entries)
    {
        Console.WriteLine($"  - {entry.Flow.Name ?? entry.EntryName}: {entry.Flow.Steps.Count} steps, {entry.Flow.RestSteps.Count} REST call(s)");
        foreach (var step in entry.Flow.RestSteps.Take(8))
        {
            Console.WriteLine($"      {ValueOrDash(step.ConnectorName)}.{ValueOrDash(step.OperationIdentifier)} -> {ValueOrDash(step.TargetVariableName)}");
        }
    }
}

static string GeneratePackageDraft(ConnectFlowPackage package, ConnectorCatalog connectorCatalog)
{
    var builder = new StringBuilder();
    builder.AppendLine("# Flow Connect package draft generated from Connect .wap export");
    builder.AppendLine($"# Source: {package.SourcePath}");
    builder.AppendLine();

    foreach (var entry in package.Entries)
    {
        builder.AppendLine("# =====================================================================");
        builder.AppendLine($"# Package entry: {entry.EntryName}");
        builder.AppendLine("# =====================================================================");
        builder.AppendLine();
        builder.AppendLine(FlowConnectDraftGenerator.Generate(entry.Flow, connectorCatalog));
    }

    return builder.ToString();
}

static string GeneratePackageWorkflowXml(ConnectFlowPackage package)
{
    var builder = new StringBuilder();
    foreach (var entry in package.Entries)
    {
        builder.AppendLine($"<!-- Package entry: {entry.EntryName} -->");
        builder.AppendLine(entry.Flow.RawWorkflowXml);
        builder.AppendLine();
    }

    return builder.ToString();
}

static void PrintConnectFlowSummary(ConnectFlowProject project)
{
    Console.WriteLine($"Flow:       {project.Name}");
    Console.WriteLine($"Origin:     {Metadata(project, "EnvironmentOrigin")}");
    Console.WriteLine($"Version:    {Metadata(project, "LatestVersion")}");
    Console.WriteLine($"Steps:      {project.Steps.Count}");
    Console.WriteLine($"Sequences:  {project.Sequences.Count}");
    Console.WriteLine($"REST calls: {project.RestSteps.Count}");
    Console.WriteLine($"DB calls:   {project.DatabaseSteps.Count}");

    var connectorGroups = project.MachineSteps
        .Where(s => !string.IsNullOrWhiteSpace(s.ConnectorName) || !string.IsNullOrWhiteSpace(s.ConnectorTypeName))
        .GroupBy(s => $"{ValueOrDash(s.ConnectorTypeName)}:{ValueOrDash(s.ConnectorName)}")
        .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

    Console.WriteLine();
    Console.WriteLine("Connector usage:");
    foreach (var group in connectorGroups)
    {
        var first = group.First();
        Console.WriteLine($"  - {ValueOrDash(first.ConnectorTypeName)} {ValueOrDash(first.ConnectorName)}: {group.Count()} step(s)");
        foreach (var step in group.Where(s => !string.IsNullOrWhiteSpace(s.OperationIdentifier)).Take(12))
        {
            Console.WriteLine($"      {step.Label ?? step.OperationDisplayName}: {step.OperationIdentifier} -> {ValueOrDash(step.TargetVariableName)}");
        }

        if (group.Count(s => !string.IsNullOrWhiteSpace(s.OperationIdentifier)) > 12)
        {
            Console.WriteLine("      ...");
        }
    }
}

static void PrintPortalExportSummary(PortalExportReport report)
{
    Console.WriteLine($"Portal export: {Path.GetFileName(report.SourcePath)}");
    Console.WriteLine($"Pages:         {report.PageCount}");
    Console.WriteLine($"Connectors:    {report.ConnectorCount}");
    Console.WriteLine($"Workflows:     {report.WorkflowCount}");
    Console.WriteLine($"LOVs:          {report.LovCount}");

    Console.WriteLine();
    Console.WriteLine("Pages and portlets:");
    foreach (var page in report.Pages)
    {
        Console.WriteLine($"- {page.Title} ({page.PortletCount} portlet{Plural(page.PortletCount)})");

        foreach (var portlet in page.Portlets)
        {
            Console.WriteLine($"  {portlet.PortletType}: {portlet.Title} ({ValueOrDash(portlet.UniqueIdentifier)})");
            Console.WriteLine($"    Connectors: {JoinOrDash(portlet.Connectors)}");
            Console.WriteLine($"    Workflows:  {JoinReferencesOrDash(portlet.Workflows)}");
            Console.WriteLine($"    LOVs:       {JoinReferencesOrDash(portlet.Lovs)}");
        }
    }
}

static string Plural(int count) => count == 1 ? string.Empty : "s";

static string JoinOrDash(IEnumerable<string> values)
{
    var materialized = values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    return materialized.Length == 0 ? "-" : string.Join(", ", materialized);
}

static string JoinReferencesOrDash(IEnumerable<PortalReference> references)
{
    var materialized = references
        .Select(r => string.Equals(r.Id, r.Name, StringComparison.OrdinalIgnoreCase) ? r.Name : $"{r.Name} ({r.Id})")
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    return materialized.Length == 0 ? "-" : string.Join(", ", materialized);
}

static string ValueOrDash(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

static string Metadata(ConnectFlowProject project, string key) =>
    project.Metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : "-";

static void PrintUsage()
{
    Console.WriteLine("""
    NCRP to Connect CLI proof-of-concept

    Usage:
      NcrpToConnect.Cli <file.ncrp|file.ncrcp|file.flow|file.wap|file.ptl|portal.json> [--xml decoded.xml] [--json inventory.json] [--flow draft.flow.txt]

    What it does:
      - Reads a .ncrp file containing Base64 text
      - Decodes Base64, decompresses gzip, and parses XML
      - Extracts connector metadata, auth schemes, containers, operations, and parameters
      - Reads Connect .flow exports with embedded WorkflowData XML
      - Reads .wap packages containing one or more Connect .flow exports
      - Extracts flow steps, sequences, connector calls, SQL, scripts, and mappings
      - Reads Portal .ptl exports or decoded portal JSON
      - Lists portal page titles, portlet counts, portlet names, connectors, workflows, and LOVs
      - Writes a first-pass Flow Script / Flow Connect draft for later mapping work

    Options:
      --xml <path>    Write the decoded XML
      --json <path>   Write the extracted connector/flow/portal inventory
      --flow <path>   Write a Flow Script draft/scaffold
      -h, --help      Show help
    """);
}

internal sealed record CliOptions(
    string? InputPath,
    string? XmlOutputPath,
    string? JsonOutputPath,
    string? FlowOutputPath,
    bool ShowHelp,
    bool HasError)
{
    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return new(null, null, null, null, ShowHelp: true, HasError: true);
        }

        string? inputPath = null;
        string? xmlOutputPath = null;
        string? jsonOutputPath = null;
        string? flowOutputPath = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    return new(null, null, null, null, ShowHelp: true, HasError: false);
                case "--xml":
                    if (!TryReadOptionValue(args, ref i, arg, out xmlOutputPath))
                    {
                        return new(null, null, null, null, ShowHelp: true, HasError: true);
                    }
                    break;
                case "--json":
                    if (!TryReadOptionValue(args, ref i, arg, out jsonOutputPath))
                    {
                        return new(null, null, null, null, ShowHelp: true, HasError: true);
                    }
                    break;
                case "--flow":
                    if (!TryReadOptionValue(args, ref i, arg, out flowOutputPath))
                    {
                        return new(null, null, null, null, ShowHelp: true, HasError: true);
                    }
                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine($"Unknown option: {arg}");
                        return new(null, null, null, null, ShowHelp: true, HasError: true);
                    }

                    if (inputPath is not null)
                    {
                        Console.Error.WriteLine($"Unexpected extra input path: {arg}");
                        return new(null, null, null, null, ShowHelp: true, HasError: true);
                    }

                    inputPath = arg;
                    break;
            }
        }

        if (inputPath is null)
        {
            Console.Error.WriteLine("Missing input file.");
            return new(null, null, null, null, ShowHelp: true, HasError: true);
        }

        return new(inputPath, xmlOutputPath, jsonOutputPath, flowOutputPath, ShowHelp: false, HasError: false);
    }

    private static bool TryReadOptionValue(string[] args, ref int index, string optionName, out string? value)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("-", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"Missing value for {optionName}.");
            value = null;
            return false;
        }

        index++;
        value = args[index];
        return true;
    }
}
