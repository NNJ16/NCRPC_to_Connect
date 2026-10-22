using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace NcrpToConnect.Cli;

internal sealed class PortalExportReader
{
    private static readonly Guid EmptyGuid = Guid.Empty;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public PortalExportReport Read(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Input portal export file was not found.", path);
        }

        var json = string.Equals(Path.GetExtension(path), ".ptl", StringComparison.OrdinalIgnoreCase)
            ? DecodePtl(path)
            : File.ReadAllText(path);

        return ReadContent(Path.GetFullPath(path), json);
    }

    public PortalExportReport ReadContent(string sourcePath, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("data", out var pagesElement) || pagesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Expected portal export JSON with a top-level data array.");
        }

        var workflowNames = ReadNameMap(root, "workflows");
        var lovNames = ReadNameMap(root, "listOfValues");

        var pages = new List<PortalPageReport>();
        foreach (var pageElement in pagesElement.EnumerateArray())
        {
            pages.Add(ReadPage(pageElement, workflowNames, lovNames));
        }

        return new PortalExportReport(
            SourcePath: sourcePath,
            PageCount: pages.Count,
            ConnectorCount: CountArray(root, "connectors"),
            WorkflowCount: CountArray(root, "workflows"),
            LovCount: CountArray(root, "listOfValues"),
            Pages: pages);
    }

    private static PortalPageReport ReadPage(
        JsonElement pageElement,
        IReadOnlyDictionary<string, string> workflowNames,
        IReadOnlyDictionary<string, string> lovNames)
    {
        var portlets = new List<PortalPortletReport>();

        if (pageElement.TryGetProperty("portletContainers", out var containersElement)
            && containersElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var containerElement in containersElement.EnumerateArray())
            {
                if (!containerElement.TryGetProperty("portlets", out var portletsElement)
                    || portletsElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var portletElement in portletsElement.EnumerateArray())
                {
                    portlets.Add(ReadPortlet(portletElement, workflowNames, lovNames));
                }
            }
        }

        return new PortalPageReport(
            Id: ReadInt(pageElement, "id"),
            UniqueIdentifier: ReadString(pageElement, "uniqueIdentifier"),
            Title: ReadString(pageElement, "title") ?? "(untitled page)",
            PortletCount: portlets.Count,
            Portlets: portlets);
    }

    private static PortalPortletReport ReadPortlet(
        JsonElement portletElement,
        IReadOnlyDictionary<string, string> workflowNames,
        IReadOnlyDictionary<string, string> lovNames)
    {
        var connectors = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var workflows = new SortedDictionary<string, PortalReference>(StringComparer.OrdinalIgnoreCase);
        var lovs = new SortedDictionary<string, PortalReference>(StringComparer.OrdinalIgnoreCase);

        var configuration = ReadString(portletElement, "configuration");
        if (!string.IsNullOrWhiteSpace(configuration))
        {
            using var configDocument = JsonDocument.Parse(configuration);
            ScanConfiguration(configDocument.RootElement, connectors, workflows, lovs, workflowNames, lovNames, []);
        }

        return new PortalPortletReport(
            Id: ReadInt(portletElement, "id"),
            UniqueIdentifier: ReadString(portletElement, "uniqueIdentifier"),
            Title: ReadString(portletElement, "title") ?? "(untitled portlet)",
            PortletType: ReadString(portletElement, "portletType") ?? "-",
            Connectors: connectors.ToArray(),
            Workflows: workflows.Values.ToArray(),
            Lovs: lovs.Values.ToArray());
    }

    private static void ScanConfiguration(
        JsonElement element,
        SortedSet<string> connectors,
        SortedDictionary<string, PortalReference> workflows,
        SortedDictionary<string, PortalReference> lovs,
        IReadOnlyDictionary<string, string> workflowNames,
        IReadOnlyDictionary<string, string> lovNames,
        IReadOnlyList<string> path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var nextPath = path.Append(property.Name).ToArray();

                    if (property.NameEquals("connectorName"))
                    {
                        AddString(connectors, property.Value);
                    }
                    else if (property.NameEquals("lovId"))
                    {
                        AddReference(lovs, property.Value, lovNames, "LOV", string.Join('.', nextPath));
                    }
                    else if (IsDirectWorkflowId(property.Name, path, property.Value))
                    {
                        AddReference(workflows, property.Value, workflowNames, "Workflow", string.Join('.', nextPath));
                    }

                    ScanConfiguration(property.Value, connectors, workflows, lovs, workflowNames, lovNames, nextPath);
                }
                break;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    ScanConfiguration(child, connectors, workflows, lovs, workflowNames, lovNames, path);
                }
                break;
        }
    }

    private static bool IsDirectWorkflowId(string propertyName, IReadOnlyList<string> path, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        if (string.Equals(propertyName, "workflowId", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.Equals(propertyName, "id", StringComparison.OrdinalIgnoreCase) || path.Count == 0)
        {
            return false;
        }

        var parent = path[^1];
        return string.Equals(parent, "workflow", StringComparison.OrdinalIgnoreCase)
            || string.Equals(parent, "crud", StringComparison.OrdinalIgnoreCase)
            || string.Equals(parent, "quickAction", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddString(SortedSet<string> values, JsonElement element)
    {
        var value = ElementString(element);
        if (!string.IsNullOrWhiteSpace(value))
        {
            values.Add(value);
        }
    }

    private static void AddReference(
        SortedDictionary<string, PortalReference> references,
        JsonElement element,
        IReadOnlyDictionary<string, string> namesById,
        string kind,
        string source)
    {
        var id = ElementString(element);
        if (string.IsNullOrWhiteSpace(id) || IsEmptyGuid(id))
        {
            return;
        }

        var name = namesById.TryGetValue(id, out var resolvedName) ? resolvedName : null;
        references.TryAdd(id, new PortalReference(id, name ?? id, kind, source));
    }

    private static Dictionary<string, string> ReadNameMap(JsonElement root, string propertyName)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty(propertyName, out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in items.EnumerateArray())
        {
            var id = ReadString(item, "id");
            var name = ReadString(item, "name");
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name) && !result.ContainsKey(id))
            {
                result[id] = name;
            }
        }

        return result;
    }

    private static int CountArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        return element.GetArrayLength();
    }

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) ? ElementString(property) : null;
    }

    private static string? ElementString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(element.GetString()) ? null : element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            _ => null
        };
    }

    private static bool IsEmptyGuid(string value)
    {
        return Guid.TryParse(value, out var guid) && guid == EmptyGuid;
    }

    private static string DecodePtl(string path)
    {
        var encryptedBytes = Convert.FromBase64String(File.ReadAllText(path).Trim());
        var decryptedBase64 = DecryptWithNovacuraUserPassword(encryptedBytes).Trim();
        var compressedBytes = Convert.FromBase64String(decryptedBase64);

        using var compressed = new MemoryStream(compressedBytes);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);

        return Encoding.Unicode.GetString(output.ToArray());
    }

    private static string DecryptWithNovacuraUserPassword(byte[] encryptedBytes)
    {
        var cryptoType = LoadNovacuraCryptoType();
        var sharedSecretEnum = cryptoType.GetNestedType("SharedSecret", BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidDataException("Novacura Crypto.SharedSecret enum was not found.");

        var userPassword = Enum.Parse(sharedSecretEnum, "UserPassword");
        var getSharedSecret = cryptoType.GetMethod("GetSharedSecret", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidDataException("Novacura Crypto.GetSharedSecret method was not found.");
        var secret = (string?)getSharedSecret.Invoke(null, [userPassword])
            ?? throw new InvalidDataException("Novacura UserPassword shared secret was empty.");

        using var input = new MemoryStream(encryptedBytes);
        using var output = new MemoryStream();
        Action<Stream> copyToOutput = stream => stream.CopyTo(output);

        var decryptStream = cryptoType.GetMethod("DecryptStream", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidDataException("Novacura Crypto.DecryptStream method was not found.");
        decryptStream.Invoke(null, [secret, input, copyToOutput, false, false]);

        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static Type LoadNovacuraCryptoType()
    {
        foreach (var candidate in NovacuraCryptoCandidates())
        {
            if (!File.Exists(candidate.Path))
            {
                continue;
            }

            try
            {
                var assembly = Assembly.LoadFrom(candidate.Path);
                var type = assembly.GetType(candidate.TypeName, throwOnError: false);
                if (type is not null)
                {
                    return type;
                }
            }
            catch
            {
                // Try the next installed Novacura location.
            }
        }

        throw new InvalidDataException("Could not load Novacura.Cryptography from the standard Novacura install folders. Decode the .ptl in Portal first, or run this app on a machine with Novacura Flow Studio/Designer installed.");
    }

    private static IEnumerable<(string Path, string TypeName)> NovacuraCryptoCandidates()
    {
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var roots = new[]
        {
            Path.Combine(programFilesX86, "Novacura"),
            Path.Combine(programFiles, "Novacura")
        };

        foreach (var root in roots.Where(Directory.Exists))
        {
            yield return (Path.Combine(root, "Flow Studio", "NovaCura.Cryptography.dll"), "NovaCura.Cryptography.Crypto");
            yield return (Path.Combine(root, "Flow Connect Designer", "NovaCura.Cryptography.dll"), "NovaCura.Cryptography.Crypto");
            yield return (Path.Combine(root, "Flow Studio", "Novacura.Framework.Cryptography.dll"), "Novacura.Framework.Cryptography.Crypto");
            yield return (Path.Combine(root, "Flow Connect Designer", "Novacura.Framework.Cryptography.dll"), "Novacura.Framework.Cryptography.Crypto");
        }
    }
}

internal sealed record PortalExportReport(
    string SourcePath,
    int PageCount,
    int ConnectorCount,
    int WorkflowCount,
    int LovCount,
    IReadOnlyList<PortalPageReport> Pages);

internal sealed record PortalPageReport(
    int? Id,
    string? UniqueIdentifier,
    string Title,
    int PortletCount,
    IReadOnlyList<PortalPortletReport> Portlets);

internal sealed record PortalPortletReport(
    int? Id,
    string? UniqueIdentifier,
    string Title,
    string PortletType,
    IReadOnlyList<string> Connectors,
    IReadOnlyList<PortalReference> Workflows,
    IReadOnlyList<PortalReference> Lovs);

internal sealed record PortalReference(
    string Id,
    string Name,
    string Kind,
    string Source);
