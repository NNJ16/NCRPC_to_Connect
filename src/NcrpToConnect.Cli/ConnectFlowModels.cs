namespace NcrpToConnect.Cli;

internal sealed record ConnectFlowProject(
    string SourcePath,
    string RawWorkflowXml,
    string? Name,
    IReadOnlyDictionary<string, string?> Metadata,
    IReadOnlyList<ConnectFlowStep> Steps,
    IReadOnlyList<ConnectFlowSequence> Sequences)
{
    public IReadOnlyList<ConnectFlowStep> MachineSteps =>
        Steps.Where(s => string.Equals(s.StepType, "MachineTaskItem", StringComparison.Ordinal)).ToArray();

    public IReadOnlyList<ConnectFlowStep> RestSteps =>
        MachineSteps.Where(s => string.Equals(s.ConnectorTypeName, "rest", StringComparison.OrdinalIgnoreCase)).ToArray();

    public IReadOnlyList<ConnectFlowStep> DatabaseSteps =>
        MachineSteps.Where(s => string.Equals(s.ConnectorTypeName, "database", StringComparison.OrdinalIgnoreCase)).ToArray();
}

internal sealed record ConnectFlowStep(
    string? Id,
    string? Label,
    string? StepType,
    string? ConfigurationType,
    string? TargetVariableName,
    string? ConnectorTypeName,
    string? ConnectorName,
    string? OperationIdentifier,
    string? OperationDisplayName,
    string? OperationDescription,
    string? QueryCode,
    string? Script,
    IReadOnlyList<ConnectFlowAssignment> Assignments,
    IReadOnlyList<ConnectFlowUserInstruction> UserInstructions,
    ConnectFlowEmail? Email,
    IReadOnlyList<ConnectFlowParameter> Inputs,
    IReadOnlyList<ConnectFlowParameter> Outputs);

internal sealed record ConnectFlowSequence(
    string? Id,
    string? FromStepId,
    string? ToStepId,
    string? Text,
    string? ConditionTag);

internal sealed record ConnectFlowParameter(
    string? Identifier,
    string? DisplayName,
    string? ParameterType,
    string? DotNetTypeName,
    bool IsMandatory,
    string? MappingMode,
    string? MappingExpression,
    string? ConstantValue,
    string? DefaultValue,
    IReadOnlyList<ConnectFlowParameter> Children);

internal sealed record ConnectFlowAssignment(
    string? TargetVariable,
    string? Expression);

internal sealed record ConnectFlowUserInstruction(
    string? InstructionType,
    string? Target,
    string? Prompt,
    bool AllowEmpty,
    string? SourcePath);

internal sealed record ConnectFlowEmail(
    string? To,
    string? Subject,
    string? Body,
    bool IsHtml);

internal sealed record ConnectFlowPackage(
    string SourcePath,
    IReadOnlyList<ConnectFlowPackageEntry> Entries);

internal sealed record ConnectFlowPackageEntry(
    string EntryName,
    ConnectFlowProject Flow);
