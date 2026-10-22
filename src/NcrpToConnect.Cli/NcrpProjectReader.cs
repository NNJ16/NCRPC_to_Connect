using System.IO.Compression;
using System.Xml.Linq;

namespace NcrpToConnect.Cli;

internal sealed class NcrpProjectReader
{
    private static readonly XNamespace SerializationNamespace = "http://schemas.microsoft.com/2003/10/Serialization/";
    private static readonly XNamespace InstanceNamespace = "http://www.w3.org/2001/XMLSchema-instance";

    public NcrpProject Read(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Input .ncrp file was not found.", path);
        }

        var base64 = File.ReadAllText(path).Trim();
        return ReadContent(Path.GetFullPath(path), base64);
    }

    public NcrpProject ReadContent(string sourcePath, string base64)
    {
        var compressedBytes = Convert.FromBase64String(base64);
        var xml = DecompressGzip(compressedBytes);
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidDataException("Decoded XML has no root element.");

        if (!NameIs(root, "ConnectorProject"))
        {
            throw new InvalidDataException($"Expected ConnectorProject XML root, found {root.Name.LocalName}.");
        }

        var index = XmlIndex.Build(document);
        var activeModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var containers = ReadContainers(root.Child("Containers"), parentRef: null, index, activeModels);

        // Some connectors place operations directly at the root level (outside of any Container).
        // Wrap those in a synthetic container so the rest of the pipeline processes them.
        var rootOperations = ReadOperations(root.Child("Operations"), index, activeModels);
        if (rootOperations.Count > 0)
        {
            var syntheticContainer = new NcrpContainer(
                Id: null,
                Name: root.ChildValue("ConnectorName") ?? "Operations",
                Description: null,
                ParentRef: null,
                Children: [],
                Operations: rootOperations);
            containers = [syntheticContainer, .. containers];
        }

        return new NcrpProject(
            SourcePath: sourcePath,
            RawXml: xml,
            ConnectorId: root.ChildValue("ConnectorId"),
            ConnectorName: root.ChildValue("ConnectorName"),
            ConnectorDescription: root.ChildValue("ConnectorDescription"),
            BasePath: root.ChildValue("BasePath"),
            AuthorizationSchemes: ReadAuthorizationSchemes(root),
            Categories: ReadCategories(root),
            Containers: containers);
    }

    private static string DecompressGzip(byte[] compressedBytes)
    {
        using var input = new MemoryStream(compressedBytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return reader.ReadToEnd();
    }

    private static IReadOnlyList<NcrpAuthorizationScheme> ReadAuthorizationSchemes(XElement root)
    {
        var schemes = root
            .Child("AuthorizationSchemes")
            ?.Elements()
            .Where(e => e.NodeType == System.Xml.XmlNodeType.Element)
            .Select(e => new NcrpAuthorizationScheme(
                Name: e.ChildValue("Name"),
                Description: e.ChildValue("Description"),
                Kind: StripNamespacePrefix(e.Attribute(InstanceNamespace + "type")?.Value),
                ApiKeyName: e.ChildValue("ApiKeyName"),
                Location: e.ChildValue("Location")))
            .ToArray();

        return schemes ?? [];
    }

    private static IReadOnlyList<NcrpCategory> ReadCategories(XElement root)
    {
        return root
            .Child("Categories")
            ?.Elements()
            .Where(e => NameIs(e, "Category"))
            .Select(e => new NcrpCategory(
                Id: e.Attribute(SerializationNamespace + "Id")?.Value,
                Name: e.ChildValue("Name"),
                ParentRef: e.Child("ParentCategory")?.Attribute(SerializationNamespace + "Ref")?.Value))
            .ToArray() ?? [];
    }

    private static IReadOnlyList<NcrpContainer> ReadContainers(XElement? containersElement, string? parentRef, XmlIndex index, HashSet<string> activeModels)
    {
        if (containersElement is null)
        {
            return [];
        }

        return containersElement
            .Elements()
            .Where(e => NameIs(e, "OperationContainer"))
            .Select(e => ReadContainer(e, parentRef, index, activeModels))
            .ToArray();
    }

    private static NcrpContainer ReadContainer(XElement element, string? parentRef, XmlIndex index, HashSet<string> activeModels)
    {
        var selfRef = element.Attribute(SerializationNamespace + "Id")?.Value;
        var directParentRef = element.Child("Container")?.Attribute(SerializationNamespace + "Ref")?.Value ?? parentRef;

        return new NcrpContainer(
            Id: element.ChildValue("Id"),
            Name: element.ChildValue("Name"),
            Description: element.ChildValue("Description"),
            ParentRef: directParentRef,
            Children: ReadContainers(element.Child("Containers"), selfRef, index, activeModels),
            Operations: ReadOperations(element.Child("Operations"), index, activeModels));
    }

    private static IReadOnlyList<NcrpOperation> ReadOperations(XElement? operationsElement, XmlIndex index, HashSet<string> activeModels)
    {
        if (operationsElement is null)
        {
            return [];
        }

        return operationsElement
            .Elements()
            .Where(e => NameIs(e, "Operation"))
            .Select(e => ReadOperation(e, index, activeModels))
            .ToArray();
    }

    private static NcrpOperation ReadOperation(XElement element, XmlIndex index, HashSet<string> activeModels)
    {
        return new NcrpOperation(
            Id: element.ChildValue("Id"),
            DisplayName: element.ChildValue("DisplayName"),
            Description: element.ChildValue("Description"),
            HttpMethod: element.ChildValue("HttpMethod"),
            Resource: element.ChildValue("Resource"),
            ContentType: element.ChildValue("ContentType"),
            IsAbsolutePath: element.ChildValue("AbsoultePath").ToBool(),
            CanBeRepeated: element.ChildValue("CanBeRepeated").ToBool(),
            IsInternal: element.ChildValue("IsInternal").ToBool(),
            InputParameters: ReadParameters(element.Child("Input")?.Child("AllParameters"), index, activeModels),
            OutputParameters: ReadParameters(element.Child("Output")?.Child("OutputParameters"), index, activeModels));
    }

    private static IReadOnlyList<NcrpParameter> ReadParameters(XElement? parametersElement, XmlIndex index, HashSet<string> activeModels)
    {
        if (parametersElement is null)
        {
            return [];
        }

        return parametersElement
            .Elements()
            .Where(e => NameIs(e, "Parameter") || NameIs(e, "OutputParameter") || NameIs(e, "ModelParameter"))
            .Select(e => ReadParameter(e, index, activeModels))
            .ToArray();
    }

    private static NcrpParameter ReadParameter(XElement element, XmlIndex index, HashSet<string> activeModels)
    {
        element = index.Dereference(element);
        int? statusCode = int.TryParse(element.ChildValue("HttpStatusCode"), out var code) ? code : null;

        return new NcrpParameter(
            Name: element.ChildValue("Name") ?? element.ChildValue("JsonName") ?? element.ChildValue("DisplayName"),
            DisplayName: element.ChildValue("DisplayName"),
            Description: element.ChildValue("Description"),
            Kind: StripNamespacePrefix(element.Attribute(InstanceNamespace + "type")?.Value),
            ParameterType: element.ChildValue("ParameterType") ?? element.ChildValue("ModelParameterType"),
            IsMandatory: ParseIsMandatory(element),
            IsArray: element.ChildValue("IsArray").ToBool(),
            DefaultValue: element.ChildValue("DefaultValue"),
            ClassName: element.ChildValue("ClassName"),
            Members: ReadModelMembers(element, index, activeModels),
            HttpStatusCode: statusCode);
    }

    private static bool ParseIsMandatory(XElement element)
    {
        var isMandatoryVal = element.ChildValue("IsMandatory") ?? element.ChildValue("IsRequired") ?? element.ChildValue("Required");
        if (!string.IsNullOrWhiteSpace(isMandatoryVal))
        {
            return isMandatoryVal.ToBool();
        }

        var nullHandling = element.ChildValue("JsonNullHandling");
        if (!string.IsNullOrWhiteSpace(nullHandling))
        {
            if (string.Equals(nullHandling, "NotRequired", StringComparison.OrdinalIgnoreCase))
                return false;
            if (string.Equals(nullHandling, "Required", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static IReadOnlyList<NcrpParameter> ReadModelMembers(XElement element, XmlIndex index, HashSet<string> activeModels)
    {
        element = index.Dereference(element);

        var modelElem = element.Child("Model");
        if (modelElem != null)
        {
            modelElem = index.Dereference(modelElem);
        }

        var membersElement = modelElem?.Child("Members")
            ?? element.Child("Members");

        if (membersElement != null)
        {
            membersElement = index.Dereference(membersElement);
        }

        IReadOnlyList<NcrpParameter> members = [];

        if (membersElement != null)
        {
            members = membersElement
                .Elements()
                .Select(index.Dereference)
                .Where(e => NameIs(e, "ModelParameter") || NameIs(e, "Parameter") || NameIs(e, "OutputParameter"))
                .Select(e => ReadParameter(e, index, activeModels))
                .ToArray();
        }

        if (members.Count == 0)
        {
            var modelName = element.ChildValue("ClassName") ?? element.ChildValue("ParameterType") ?? element.ChildValue("ModelParameterType");
            if (!string.IsNullOrWhiteSpace(modelName)
                && !modelName.Equals("Object", StringComparison.OrdinalIgnoreCase)
                && !modelName.Equals("String", StringComparison.OrdinalIgnoreCase)
                && !modelName.Equals("Nope", StringComparison.OrdinalIgnoreCase)
                && !modelName.Equals("None", StringComparison.OrdinalIgnoreCase)
                && activeModels.Add(modelName))
            {
                try
                {
                    var matchingModel = index.FindModelByName(modelName);

                    if (matchingModel != null && matchingModel != element && matchingModel != modelElem)
                    {
                        var matchingMembers = matchingModel.Child("Members");
                        if (matchingMembers != null)
                        {
                            matchingMembers = index.Dereference(matchingMembers);
                            members = matchingMembers
                                .Elements()
                                .Select(index.Dereference)
                                .Where(e => NameIs(e, "ModelParameter") || NameIs(e, "Parameter") || NameIs(e, "OutputParameter"))
                                .Select(e => ReadParameter(e, index, activeModels))
                                .ToArray();
                        }
                    }
                }
                finally
                {
                    activeModels.Remove(modelName);
                }
            }
        }

        return members;
    }

    private sealed class XmlIndex
    {
        private readonly Dictionary<string, XElement> _elementsById;
        private readonly Dictionary<string, XElement> _modelsByName;

        public static XmlIndex Build(XDocument document)
        {
            var elementsById = new Dictionary<string, XElement>(StringComparer.Ordinal);
            var modelsByName = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);

            foreach (var element in document.Descendants())
            {
                var idAttr = element.Attributes().FirstOrDefault(a => a.Name.LocalName == "Id")?.Value;
                if (!string.IsNullOrEmpty(idAttr) && !elementsById.ContainsKey(idAttr))
                {
                    elementsById[idAttr] = element;
                }
            }

            foreach (var element in document.Descendants())
            {
                var localName = element.Name.LocalName;
                if (string.Equals(localName, "Model", StringComparison.Ordinal) || string.Equals(localName, "EnumModel", StringComparison.Ordinal))
                {
                    var dereferenced = DereferenceStatic(element, elementsById, visited: []);
                    var name = dereferenced.ChildValue("Name") ?? dereferenced.ChildValue("DisplayName");
                    if (!string.IsNullOrWhiteSpace(name) && !modelsByName.ContainsKey(name))
                    {
                        modelsByName[name] = dereferenced;
                    }
                }
            }

            return new XmlIndex(elementsById, modelsByName);
        }

        private XmlIndex(Dictionary<string, XElement> elementsById, Dictionary<string, XElement> modelsByName)
        {
            _elementsById = elementsById;
            _modelsByName = modelsByName;
        }

        public XElement Dereference(XElement element)
        {
            return DereferenceStatic(element, _elementsById, visited: []);
        }

        private static XElement DereferenceStatic(XElement element, Dictionary<string, XElement> elementsById, HashSet<string> visited)
        {
            var refAttr = element.Attributes().FirstOrDefault(a => a.Name.LocalName == "Ref")?.Value;
            if (string.IsNullOrEmpty(refAttr) || !visited.Add(refAttr))
            {
                return element;
            }

            if (elementsById.TryGetValue(refAttr, out var target) && target != element)
            {
                return DereferenceStatic(target, elementsById, visited);
            }

            return element;
        }

        public XElement? FindModelByName(string name)
        {
            return _modelsByName.TryGetValue(name, out var model) ? model : null;
        }
    }

    private static bool NameIs(XElement element, string localName) =>
        string.Equals(element.Name.LocalName, localName, StringComparison.Ordinal);

    private static string? StripNamespacePrefix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var separatorIndex = value.IndexOf(':', StringComparison.Ordinal);
        return separatorIndex >= 0 ? value[(separatorIndex + 1)..] : value;
    }
}

internal static class XElementExtensions
{
    public static XElement? Child(this XElement element, string localName) =>
        element.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, localName, StringComparison.Ordinal));

    public static IEnumerable<XElement> Children(this XElement element, string localName) =>
        element.Elements().Where(e => string.Equals(e.Name.LocalName, localName, StringComparison.Ordinal));

    public static IEnumerable<XElement> DescendantsNamed(this XElement element, string localName) =>
        element.Descendants().Where(e => string.Equals(e.Name.LocalName, localName, StringComparison.Ordinal));

    public static string? ChildValue(this XElement element, string localName)
    {
        var value = element.Child(localName)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public static bool ToBool(this string? value) =>
        bool.TryParse(value, out var result) && result;
}
