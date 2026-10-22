using System.Xml.Linq;

namespace NcrpToConnect.Cli;

internal sealed class ConnectFlowReader
{
    private static readonly XNamespace InstanceNamespace = "http://www.w3.org/2001/XMLSchema-instance";

    public ConnectFlowProject Read(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Input .flow file was not found.", path);
        }

        return ReadXml(Path.GetFullPath(path), File.ReadAllText(path));
    }

    public ConnectFlowProject ReadXml(string sourcePath, string xml)
    {
        var outerDocument = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var root = outerDocument.Root ?? throw new InvalidDataException("Flow export XML has no root element.");
        if (!string.Equals(root.Name.LocalName, "ExportInformation", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Expected ExportInformation XML root, found {root.Name.LocalName}.");
        }

        var workflowXml = root.ChildValue("WorkflowData")
            ?? throw new InvalidDataException("Flow export does not contain WorkflowData.");
        var workflowDocument = XDocument.Parse(workflowXml, LoadOptions.PreserveWhitespace);
        var workflowRoot = workflowDocument.Root ?? throw new InvalidDataException("WorkflowData XML has no root element.");
        var baseElements = workflowRoot.Child("Elements")?.Children("Base").ToArray() ?? [];
        var labelsByParentId = ReadLabels(baseElements);

        return new ConnectFlowProject(
            SourcePath: sourcePath,
            RawWorkflowXml: workflowXml,
            Name: root.ChildValue("Name"),
            Metadata: ReadMetadata(root),
            Steps: ReadSteps(baseElements, labelsByParentId),
            Sequences: ReadSequences(baseElements));
    }

    private static IReadOnlyDictionary<string, string?> ReadMetadata(XElement root)
    {
        return root
            .Child("ApplicationMetaData")
            ?.Children("ApplicationMetaDataEntry")
            .Select(e => new
            {
                Key = e.ChildValue("Key"),
                Value = e.ChildValue("Value")
            })
            .Where(e => !string.IsNullOrWhiteSpace(e.Key))
            .GroupBy(e => e.Key!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string?> ReadLabels(IReadOnlyList<XElement> baseElements)
    {
        return baseElements
            .Where(e => string.Equals(GetBaseKind(e), "Label", StringComparison.Ordinal))
            .Select(e => new
            {
                ParentItemId = e.ChildValue("ParentItemId"),
                Text = e.ChildValue("Text")
            })
            .Where(e => !string.IsNullOrWhiteSpace(e.ParentItemId))
            .GroupBy(e => e.ParentItemId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Text, StringComparer.Ordinal);
    }

    private static IReadOnlyList<ConnectFlowStep> ReadSteps(IReadOnlyList<XElement> baseElements, IReadOnlyDictionary<string, string?> labelsByParentId)
    {
        return baseElements
            .Where(e =>
            {
                var baseKind = GetBaseKind(e);
                var stepType = e.ChildValue("Type");
                return !string.Equals(baseKind, "Label", StringComparison.Ordinal)
                    && !string.Equals(baseKind, "Annotation", StringComparison.Ordinal)
                    && !string.Equals(stepType, "Sequence", StringComparison.Ordinal);
            })
            .Select(e => ReadStep(e, labelsByParentId))
            .ToArray();
    }

    private static ConnectFlowStep ReadStep(XElement element, IReadOnlyDictionary<string, string?> labelsByParentId)
    {
        var id = element.ChildValue("Id");
        var tag = element.Child("Tag");
        var command = tag?.Child("CommandElement")?.DescendantsNamed("command").FirstOrDefault();
        var assemblyMethod = command?.DescendantsNamed("AssemblyMethodDescription").FirstOrDefault();

        return new ConnectFlowStep(
            Id: id,
            Label: id is not null && labelsByParentId.TryGetValue(id, out var label) ? label : element.ChildValue("Text"),
            StepType: element.ChildValue("Type") ?? GetBaseKind(element),
            ConfigurationType: StripNamespacePrefix(tag?.Attribute(InstanceNamespace + "type")?.Value),
            TargetVariableName: tag?.ChildValue("TargetVariableName"),
            ConnectorTypeName: tag?.ChildValue("ConnectorTypeName"),
            ConnectorName: command?.Attribute("connector")?.Value,
            OperationIdentifier: assemblyMethod?.ChildValue("Identifier"),
            OperationDisplayName: assemblyMethod?.ChildValue("DisplayName"),
            OperationDescription: assemblyMethod?.ChildValue("Description"),
            QueryCode: command?.DescendantsNamed("code").FirstOrDefault()?.Value.Trim(),
            Script: tag?.ChildValue("Script"),
            Assignments: ReadAssignments(tag),
            UserInstructions: ReadUserInstructions(tag),
            Email: ReadEmail(command),
            Inputs: ReadParameters(assemblyMethod?.Child("Inputs"), input: true),
            Outputs: ReadParameters(assemblyMethod?.Child("Outputs"), input: false));
    }

    private static IReadOnlyList<ConnectFlowAssignment> ReadAssignments(XElement? tag)
    {
        return tag
            ?.Child("Assignments")
            ?.Children("Assignment")
            .Select(e => new ConnectFlowAssignment(
                TargetVariable: e.ChildValue("TargetVariable"),
                Expression: e.ChildValue("Expression")))
            .ToArray() ?? [];
    }

    private static IReadOnlyList<ConnectFlowUserInstruction> ReadUserInstructions(XElement? tag)
    {
        return tag
            ?.Child("Instructions")
            ?.Children("InstructionBase")
            .Select(e => new ConnectFlowUserInstruction(
                InstructionType: StripNamespacePrefix(e.Attribute(InstanceNamespace + "type")?.Value),
                Target: e.ChildValue("Target"),
                Prompt: ReadLocalizedText(e.Child("Prompt")),
                AllowEmpty: e.ChildValue("AllowEmpty").ToBool(),
                SourcePath: e.Child("Source")?.ChildValue("Path")))
            .ToArray() ?? [];
    }

    private static ConnectFlowEmail? ReadEmail(XElement? command)
    {
        var emailConfig = command?.DescendantsNamed("EmailTaskConfig").FirstOrDefault();
        if (emailConfig is null)
        {
            return null;
        }

        return new ConnectFlowEmail(
            To: emailConfig.ChildValue("To"),
            Subject: emailConfig.ChildValue("Subject"),
            Body: emailConfig.ChildValue("Body"),
            IsHtml: emailConfig.ChildValue("IsHtml").ToBool());
    }

    private static string? ReadLocalizedText(XElement? localized)
    {
        var languageStrings = localized?.Child("Strings")?.Children("LanguageString").ToArray() ?? [];
        return languageStrings.FirstOrDefault(e => string.Equals(e.ChildValue("LanguageCode"), "sv", StringComparison.OrdinalIgnoreCase))?.ChildValue("Text")
            ?? languageStrings.FirstOrDefault(e => string.Equals(e.ChildValue("LanguageCode"), "en", StringComparison.OrdinalIgnoreCase))?.ChildValue("Text")
            ?? languageStrings.Select(e => e.ChildValue("Text")).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
    }

    private static IReadOnlyList<ConnectFlowSequence> ReadSequences(IReadOnlyList<XElement> baseElements)
    {
        return baseElements
            .Where(e => string.Equals(e.ChildValue("Type"), "Sequence", StringComparison.Ordinal))
            .Select(e => new ConnectFlowSequence(
                Id: e.ChildValue("Id"),
                FromStepId: e.ChildValue("SinkItemId"),
                ToStepId: e.ChildValue("TargetItemId"),
                Text: e.ChildValue("Text"),
                ConditionTag: e.ChildValue("Tag")))
            .ToArray();
    }

    private static IReadOnlyList<ConnectFlowParameter> ReadParameters(XElement? listElement, bool input)
    {
        var items = listElement?.Child("_items");
        if (items is null)
        {
            return [];
        }

        var names = input
            ? new[] { "InputParameterDescription", "MethodParameterDescription" }
            : new[] { "OutputParamterDescription", "MethodParameterDescription" };

        return items
            .Elements()
            .Where(e => names.Contains(e.Name.LocalName, StringComparer.Ordinal))
            .Where(e => e.Child("DisplayName") is not null || e.Child("Identifier") is not null)
            .Select(e => ReadParameter(e, input))
            .ToArray();
    }

    private static ConnectFlowParameter ReadParameter(XElement element, bool input)
    {
        var runtimeValue = element.Child("RuntimeValue");
        return new ConnectFlowParameter(
            Identifier: element.ChildValue("Identifier"),
            DisplayName: element.ChildValue("DisplayName"),
            ParameterType: element.ChildValue("Type"),
            DotNetTypeName: element.ChildValue("DotNetTypeName"),
            IsMandatory: element.ChildValue("IsMandatory").ToBool(),
            MappingMode: runtimeValue?.ChildValue("Mode"),
            MappingExpression: runtimeValue?.ChildValue("MappingExpression"),
            ConstantValue: runtimeValue?.ChildValue("Value"),
            DefaultValue: element.ChildValue("DefaultValue"),
            Children: ReadParameters(element.Child("ComplexContents"), input));
    }

    private static string? GetBaseKind(XElement element) =>
        StripNamespacePrefix(element.Attribute(InstanceNamespace + "type")?.Value);

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
