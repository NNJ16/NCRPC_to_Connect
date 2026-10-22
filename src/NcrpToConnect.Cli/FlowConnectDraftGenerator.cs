using System.Text;
using System.Text.RegularExpressions;

namespace NcrpToConnect.Cli;

internal static partial class FlowConnectDraftGenerator
{
    public static string Generate(ConnectFlowProject project, ConnectorCatalog? connectorCatalog = null)
    {
        var builder = new StringBuilder();
        var context = CreateContext(project, connectorCatalog);
        builder.AppendLine("# Flow Connect draft generated from Connect .flow export");
        builder.AppendLine($"# Source: {project.SourcePath}");
        builder.AppendLine($"# Flow: {project.Name}");
        builder.AppendLine($"# Origin Flow version: {Metadata(project, "OriginFlowVersion")}");
        builder.AppendLine("#");
        builder.AppendLine("# This draft keeps the original step order, labels, connector calls, SQL,");
        builder.AppendLine("# script snippets, and branch labels. TODOs mark target syntax that still");
        builder.AppendLine("# needs exact Flow Connect runtime mapping.");
        builder.AppendLine();
        builder.AppendLine($"flow {Quote(project.Name ?? "Unnamed flow")}");
        builder.AppendLine("begin");

        foreach (var entry in OrderedSteps(project))
        {
            if (entry.Incoming is not null)
            {
                var route = entry.Incoming.Text ?? UsefulConditionTag(entry.Incoming.ConditionTag);
                if (!string.IsNullOrWhiteSpace(route))
                {
                    builder.AppendLine($"    # Route: {route}");
                }
            }

            AppendStep(builder, entry.Step, context);
        }

        builder.AppendLine("end");
        return builder.ToString();
    }

    private static DraftContext CreateContext(ConnectFlowProject project, ConnectorCatalog? connectorCatalog)
    {
        var payloadWrapperByVariable = project.RestSteps
            .Where(step => !string.IsNullOrWhiteSpace(step.TargetVariableName))
            .Select(step => new
            {
                Variable = step.TargetVariableName!,
                Payload = FindPayloadParameter(step)
            })
            .Where(step => !string.IsNullOrWhiteSpace(step.Payload?.Identifier ?? step.Payload?.DisplayName))
            .GroupBy(step => step.Variable, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First().Payload!.Identifier ?? group.First().Payload!.DisplayName!,
                StringComparer.Ordinal);

        var responseEnvelopeVariables = project.RestSteps
            .Where(step => !string.IsNullOrWhiteSpace(step.TargetVariableName))
            .Where(step => connectorCatalog?.FindOperation(step.ConnectorName, step.OperationIdentifier ?? step.OperationDisplayName) is not null)
            .Select(step => step.TargetVariableName!)
            .ToHashSet(StringComparer.Ordinal);

        return new DraftContext(payloadWrapperByVariable, responseEnvelopeVariables, connectorCatalog);
    }

    private static void AppendStep(StringBuilder builder, ConnectFlowStep step, DraftContext context)
    {
        var title = step.Label ?? step.OperationDisplayName ?? step.OperationIdentifier ?? step.StepType ?? step.Id ?? "Step";
        builder.AppendLine($"    # Step {step.Id}: {title} [{step.StepType}]");

        if (string.Equals(step.StepType, "InitialStepItem", StringComparison.Ordinal))
        {
            builder.AppendLine("    # Start");
            builder.AppendLine();
            return;
        }

        if (string.Equals(step.StepType, "EndItem", StringComparison.Ordinal))
        {
            builder.AppendLine("    return;");
            builder.AppendLine();
            return;
        }

        if (string.Equals(step.ConnectorTypeName, "rest", StringComparison.OrdinalIgnoreCase))
        {
            AppendRestStep(builder, step, context);
            return;
        }

        if (string.Equals(step.ConnectorTypeName, "database", StringComparison.OrdinalIgnoreCase))
        {
            AppendDatabaseStep(builder, step);
            return;
        }

        if (string.Equals(step.ConnectorTypeName, "email", StringComparison.OrdinalIgnoreCase))
        {
            AppendEmailStep(builder, step);
            return;
        }

        if (string.Equals(step.StepType, "ScriptTaskItem", StringComparison.Ordinal))
        {
            AppendScriptStep(builder, step, context);
            return;
        }

        if (string.Equals(step.StepType, "AssignmentItem", StringComparison.Ordinal))
        {
            AppendAssignmentStep(builder, step, context);
            return;
        }

        if (string.Equals(step.StepType, "UserTaskItem", StringComparison.Ordinal))
        {
            AppendUserStep(builder, step);
            return;
        }

        builder.AppendLine($"    # TODO: Translate {step.StepType} configuration.");
        if (!string.IsNullOrWhiteSpace(step.TargetVariableName))
        {
            builder.AppendLine($"    # Target variable: {step.TargetVariableName}");
        }

        builder.AppendLine();
    }

    private static void AppendRestStep(StringBuilder builder, ConnectFlowStep step, DraftContext context)
    {
        var http = ResolveHttpRequest(step, context);
        var bodyInputs = FlattenInputs(step.Inputs)
            .Where(input => !IsHeaderLike(input))
            .ToArray();
        var headers = ResolveHeaders(step, http);
        var payload = FindPayloadParameter(step);
        var successStatusCode = ResolveSuccessStatusCode(payload);
        var responseType = payload is not null || http.UseResponseEnvelope
            ? ResolveResponseType(payload, http)
            : null;

        builder.AppendLine("    // Flow Designer generated code");
        builder.AppendLine($"    let url = `{http.Url}`;");
        builder.AppendLine("    let headers = {");
        foreach (var header in headers)
        {
            builder.AppendLine($"        {QuoteJsonProperty(header.Name)}: `{header.Value}`,");
        }
        builder.AppendLine("    };");

        if (http.HasUnresolvedPath)
        {
            builder.AppendLine($"    // TODO: Verify Classic operation URL for {step.ConnectorName}.{step.OperationIdentifier}.");
        }

        foreach (var input in bodyInputs.Where(input => input.MappingMode is not null))
        {
            builder.AppendLine($"    // Classic input {input.DisplayName ?? input.Identifier}: {input.MappingMode} {RenderValue(input)}");
        }

        builder.AppendLine("    let jsonBody = JSON.Serialize(");
        builder.AppendLine("    {");
        foreach (var input in bodyInputs)
        {
            builder.AppendLine($"        {QuoteJsonProperty(input.DisplayName ?? input.Identifier ?? "parameter")}: {RenderValue(input)},");
        }
        builder.AppendLine("    }");
        builder.AppendLine("    );");
        builder.AppendLine($"    let response = HTTP.{http.FunctionName}({BuildHttpArguments(http)});");
        builder.AppendLine("    let success = response.statusCode >= 200 and response.statusCode <= 299;");

        if (responseType is not null)
        {
            builder.AppendLine();
            AppendResponseType(builder, responseType);
            builder.AppendLine(http.UseResponseEnvelope
                ? "    let data = case when success then JSON.deserialize<Response>(response.contents) else null end;"
                : successStatusCode is null
                    ? "    let data = case when success then JSON.deserialize<Response>(response.contents) else null end;"
                    : $"    let data = case when response.statusCode = {successStatusCode} then JSON.deserialize<Response>(response.contents) else null end;");

            if (http.UseResponseEnvelope)
            {
                builder.AppendLine("    return { contents: data, statusCode: response.statusCode, reasonPhrase: response.reasonPhrase, headers: response.headers }");
            }
            else
            {
                builder.AppendLine("    return data");
            }
        }
        else
        {
            builder.AppendLine("    return response");
        }

        builder.AppendLine();
    }

    private static void AppendDatabaseStep(StringBuilder builder, ConnectFlowStep step)
    {
        var target = SanitizeIdentifier(step.TargetVariableName ?? "QueryResult");
        builder.AppendLine($"    {target} := Connect.Database.Query(");
        builder.AppendLine($"        connector: {Quote(step.ConnectorName ?? "unknown")},");
        builder.AppendLine("        sql: \"\"\"");
        builder.AppendLine(IndentBlock(step.QueryCode ?? string.Empty, spaces: 8));
        builder.AppendLine("        \"\"\"");
        builder.AppendLine("    );");
        builder.AppendLine();
    }

    private static void AppendScriptStep(StringBuilder builder, ConnectFlowStep step, DraftContext context)
    {
        if (!string.IsNullOrWhiteSpace(step.TargetVariableName))
        {
            builder.AppendLine($"    # Target variable: {step.TargetVariableName}");
        }

        builder.AppendLine("    # Original Flow script:");
        builder.AppendLine(IndentBlock(NormalizeScriptForInlineRestPayload(step.Script ?? string.Empty, context), spaces: 4));
        builder.AppendLine();
    }

    private static void AppendAssignmentStep(StringBuilder builder, ConnectFlowStep step, DraftContext context)
    {
        foreach (var assignment in step.Assignments)
        {
            builder.AppendLine($"    {SanitizeIdentifier(assignment.TargetVariable ?? "value")} := {NormalizeRestWrapperReferences(RenderExpression(assignment.Expression), context)};");
        }

        if (step.Assignments.Count == 0)
        {
            builder.AppendLine("    # TODO: Translate empty assignment configuration.");
        }

        builder.AppendLine();
    }

    private static void AppendUserStep(StringBuilder builder, ConnectFlowStep step)
    {
        var displayOnlyCount = 0;
        foreach (var instruction in step.UserInstructions)
        {
            if (string.IsNullOrWhiteSpace(instruction.Target))
            {
                if (!string.IsNullOrWhiteSpace(instruction.Prompt))
                {
                    builder.AppendLine($"    # UI {instruction.InstructionType}: {instruction.Prompt}");
                }
                else
                {
                    displayOnlyCount++;
                }

                continue;
            }

            var target = SanitizeIdentifier(instruction.Target ?? "input");
            builder.AppendLine($"    {target} := Flow.UserInput(");
            builder.AppendLine($"        type: {Quote(instruction.InstructionType ?? "unknown")},");
            builder.AppendLine($"        prompt: {Quote(instruction.Prompt ?? instruction.Target ?? string.Empty)},");
            builder.AppendLine($"        required: {BoolLiteral(!instruction.AllowEmpty)}");

            if (!string.IsNullOrWhiteSpace(instruction.SourcePath))
            {
                builder.AppendLine($"        , source: {Quote(instruction.SourcePath)}");
            }

            builder.AppendLine("    );");
        }

        if (displayOnlyCount > 0)
        {
            builder.AppendLine($"    # {displayOnlyCount} display-only UI instruction(s) omitted from executable draft.");
        }

        if (step.UserInstructions.Count == 0)
        {
            builder.AppendLine("    # TODO: Translate user task configuration.");
        }

        builder.AppendLine();
    }

    private static void AppendEmailStep(StringBuilder builder, ConnectFlowStep step)
    {
        if (step.Email is null)
        {
            builder.AppendLine("    # TODO: Translate email connector configuration.");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("    Flow.Email.Send(");
        builder.AppendLine($"        to: {Quote(step.Email.To ?? string.Empty)},");
        builder.AppendLine($"        subject: {Quote(step.Email.Subject ?? string.Empty)},");
        builder.AppendLine($"        body: {Quote(step.Email.Body ?? string.Empty)},");
        builder.AppendLine($"        isHtml: {BoolLiteral(step.Email.IsHtml)}");
        builder.AppendLine("    );");
        builder.AppendLine();
    }

    private static IEnumerable<ConnectFlowParameter> FlattenInputs(IEnumerable<ConnectFlowParameter> parameters)
    {
        foreach (var parameter in parameters)
        {
            if (parameter.Children.Count == 0)
            {
                yield return parameter;
                continue;
            }

            foreach (var child in FlattenInputs(parameter.Children))
            {
                yield return child;
            }
        }
    }

    private static ConnectFlowParameter? FindPayloadParameter(ConnectFlowStep step)
    {
        var operationOutput = step.Outputs.FirstOrDefault();
        return operationOutput?.Children.FirstOrDefault(parameter =>
            !IsTransportOutput(parameter)
            && string.Equals(parameter.ParameterType, "Record", StringComparison.OrdinalIgnoreCase)
            && parameter.Children.Count > 0);
    }

    private static bool IsTransportOutput(ConnectFlowParameter parameter)
    {
        var name = parameter.DisplayName ?? parameter.Identifier;
        return string.Equals(name, "HttpStatusCode", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "ReasonPhrase", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "AllHeaders", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHeaderLike(ConnectFlowParameter parameter)
    {
        var name = parameter.DisplayName ?? parameter.Identifier ?? string.Empty;
        return name.Contains("header", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase);
    }

    private static int? ResolveSuccessStatusCode(ConnectFlowParameter? payload)
    {
        var name = payload?.DisplayName ?? payload?.Identifier;
        return name switch
        {
            "OK" => 200,
            "Created" => 201,
            "Accepted" => 202,
            "NoContent" => 204,
            "No Content" => 204,
            _ => null
        };
    }

    private static HttpRequestDraft ResolveHttpRequest(ConnectFlowStep step, DraftContext context)
    {
        var connectorName = step.ConnectorName ?? "Connector";
        var operationName = step.OperationIdentifier ?? step.OperationDisplayName ?? "Operation";
        var environmentName = ResolveEnvironmentName(connectorName);
        var catalogOperation = context.ConnectorCatalog?.FindOperation(connectorName, operationName);

        if (catalogOperation is not null)
        {
            var operation = catalogOperation.Operation;
            return new(
                FunctionName: ResolveHttpFunctionName(operation.HttpMethod),
                Url: operation.Resource ?? $"/{{environment.{environmentName}}}/TODO/{operationName}",
                HasUnresolvedPath: string.IsNullOrWhiteSpace(operation.Resource),
                ContentType: operation.ContentType,
                UseResponseEnvelope: true,
                IsSalesforce: IsSalesforceOperation(operation),
                UseSalesforcePublishResponse: IsSalesforcePublishOperation(operation));
        }

        if (string.Equals(connectorName, "CabasPrd", StringComparison.OrdinalIgnoreCase)
            && string.Equals(operationName, "MessagesPeekLock", StringComparison.OrdinalIgnoreCase))
        {
            return new("postText", $"/{{environment.{environmentName}}}/messages/head", HasUnresolvedPath: false, ContentType: null, UseResponseEnvelope: false, IsSalesforce: false, UseSalesforcePublishResponse: false);
        }

        var functionName = operationName.StartsWith("Get", StringComparison.OrdinalIgnoreCase)
            ? "getText"
            : operationName.StartsWith("Delete", StringComparison.OrdinalIgnoreCase)
                ? "deleteText"
                : "postText";

        return new(functionName, $"/{{environment.{environmentName}}}/TODO/{operationName}", HasUnresolvedPath: true, ContentType: null, UseResponseEnvelope: false, IsSalesforce: false, UseSalesforcePublishResponse: false);
    }

    private static string ResolveHttpFunctionName(string? httpMethod) =>
        httpMethod?.ToUpperInvariant() switch
        {
            "GET" => "getText",
            "DELETE" => "deleteText",
            "PATCH" => "patchText",
            "PUT" => "putText",
            _ => "postText"
        };

    private static bool IsSalesforceOperation(NcrpOperation operation) =>
        operation.Resource?.Contains("/services/data/", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsSalesforcePublishOperation(NcrpOperation operation) =>
        string.Equals(operation.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase)
        && operation.Resource?.Contains("/services/data/", StringComparison.OrdinalIgnoreCase) == true
        && operation.Resource.Contains("/sobjects/", StringComparison.OrdinalIgnoreCase);

    private static string ResolveEnvironmentName(string connectorName)
    {
        var normalized = connectorName.EndsWith("Prd", StringComparison.OrdinalIgnoreCase)
            ? connectorName[..^3]
            : connectorName;

        return $"{SanitizeIdentifier(normalized)}BaseURL";
    }

    private static string BuildHttpArguments(HttpRequestDraft http) =>
        http.FunctionName switch
        {
            "getText" => "url, headers",
            "deleteText" => "url, headers",
            _ => "url, headers, jsonBody"
        };

    private static IReadOnlyList<HttpHeaderDraft> ResolveHeaders(ConnectFlowStep step, HttpRequestDraft http)
    {
        var headers = new List<HttpHeaderDraft>();

        if (!string.IsNullOrWhiteSpace(http.ContentType)
            && !headers.Any(header => string.Equals(header.Name, "Content-Type", StringComparison.OrdinalIgnoreCase)))
        {
            headers.Insert(0, new HttpHeaderDraft("Content-Type", http.ContentType!));
        }

        return headers;
    }

    private static ResponseTypeDraft? ResolveResponseType(ConnectFlowParameter? payload, HttpRequestDraft http)
    {
        if (http.UseSalesforcePublishResponse)
        {
            return new ResponseTypeDraft(null, UseSalesforceDefault: true);
        }

        if (payload is not null)
        {
            return new ResponseTypeDraft(payload, UseSalesforceDefault: false);
        }

        return http.IsSalesforce ? new ResponseTypeDraft(null, UseSalesforceDefault: true) : null;
    }

    private static void AppendResponseType(StringBuilder builder, ResponseTypeDraft responseType)
    {
        builder.AppendLine("    type Response = ");
        if (responseType.Parameter is not null)
        {
            AppendTypeLiteral(builder, responseType.Parameter, indent: 4, renderName: false, trailingComma: false);
            builder.AppendLine("    ;");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("    {");
        builder.AppendLine("        id: text,");
        builder.AppendLine("        success: boolean,");
        builder.AppendLine("        errors: {");
        builder.AppendLine("            statusCode: text,");
        builder.AppendLine("            message: text,");
        builder.AppendLine("            fields: {}*?");
        builder.AppendLine("        }*");
        builder.AppendLine("    };");
        builder.AppendLine();
    }

    private static void AppendTypeLiteral(StringBuilder builder, ConnectFlowParameter parameter, int indent, bool renderName, bool trailingComma)
    {
        var padding = new string(' ', indent);
        if (renderName)
        {
            builder.Append($"{padding}{SanitizeIdentifier(parameter.DisplayName ?? parameter.Identifier ?? "value")}: ");
        }
        else
        {
            builder.Append(padding);
        }

        if (parameter.Children.Count > 0)
        {
            builder.AppendLine("{");
            foreach (var child in parameter.Children)
            {
                AppendTypeLiteral(builder, child, indent + 4, renderName: true, trailingComma: true);
            }

            builder.Append(padding);
            builder.Append("}");
            if (string.Equals(parameter.ParameterType, "Table", StringComparison.OrdinalIgnoreCase))
            {
                builder.Append('*');
            }
            else if (IsNullable(parameter))
            {
                builder.Append('?');
            }

            if (trailingComma)
            {
                builder.Append(',');
            }

            builder.AppendLine();
            return;
        }

        builder.Append(FlowType(parameter));
        if (IsNullable(parameter))
        {
            builder.Append('?');
        }

        if (trailingComma)
        {
            builder.Append(',');
        }

        builder.AppendLine();
    }

    private static string NormalizeScriptForInlineRestPayload(string script, DraftContext context)
    {
        var result = NormalizeRestWrapperReferences(script, context);
        return result;
    }

    private static string NormalizeRestWrapperReferences(string script, DraftContext context)
    {
        var result = script;
        foreach (var wrapper in context.PayloadWrapperByVariable)
        {
            if (context.ResponseEnvelopeVariables.Contains(wrapper.Key))
            {
                result = result.Replace($"{wrapper.Key}.{wrapper.Value}.", $"{wrapper.Key}.contents.", StringComparison.Ordinal);
            }
            else
            {
                result = result.Replace($"{wrapper.Key}.{wrapper.Value}.", $"{wrapper.Key}.", StringComparison.Ordinal);
                result = result.Replace($"Return map {wrapper.Key} as [", "Return [", StringComparison.Ordinal);
                result = result.Replace($"{wrapper.Value}.", $"{wrapper.Key}.", StringComparison.Ordinal);
            }
        }

        return result;
    }

    private static string FlowType(ConnectFlowParameter parameter)
    {
        var dotNetType = parameter.DotNetTypeName ?? string.Empty;
        if (dotNetType.Contains("Boolean", StringComparison.OrdinalIgnoreCase))
        {
            return "boolean";
        }

        if (dotNetType.Contains("DateTime", StringComparison.OrdinalIgnoreCase))
        {
            return "datetime";
        }

        if (dotNetType.Contains("Int16", StringComparison.OrdinalIgnoreCase)
            || dotNetType.Contains("Int32", StringComparison.OrdinalIgnoreCase)
            || dotNetType.Contains("Int64", StringComparison.OrdinalIgnoreCase)
            || dotNetType.Contains("Decimal", StringComparison.OrdinalIgnoreCase)
            || dotNetType.Contains("Double", StringComparison.OrdinalIgnoreCase)
            || dotNetType.Contains("Single", StringComparison.OrdinalIgnoreCase))
        {
            return "number";
        }

        return "text";
    }

    private static bool IsNullable(ConnectFlowParameter parameter)
    {
        var dotNetType = parameter.DotNetTypeName ?? string.Empty;
        return dotNetType.Contains("Nullable`1", StringComparison.Ordinal)
            || dotNetType.StartsWith("System.Object", StringComparison.Ordinal);
    }

    private static string RenderValue(ConnectFlowParameter parameter)
    {
        if (!string.IsNullOrWhiteSpace(parameter.MappingExpression))
        {
            return parameter.MappingExpression.StartsWith('{') && parameter.MappingExpression.EndsWith('}')
                ? parameter.MappingExpression.Trim('{', '}')
                : parameter.MappingExpression;
        }

        if (!string.IsNullOrWhiteSpace(parameter.ConstantValue))
        {
            return Quote(parameter.ConstantValue);
        }

        if (!string.IsNullOrWhiteSpace(parameter.DefaultValue))
        {
            return Quote(parameter.DefaultValue);
        }

        return "null";
    }

    private static string RenderExpression(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return "null";
        }

        if (!expression.Contains('{', StringComparison.Ordinal))
        {
            return expression;
        }

        var matches = ClassicExpressionSegments().Matches(expression);
        if (matches.Count == 1
            && matches[0].Index == 0
            && matches[0].Length == expression.Length)
        {
            return matches[0].Groups[1].Value;
        }

        var parts = new List<string>();
        var cursor = 0;
        foreach (Match match in matches)
        {
            if (match.Index > cursor)
            {
                AddLiteral(expression[cursor..match.Index]);
            }

            parts.Add(match.Groups[1].Value);
            cursor = match.Index + match.Length;
        }

        if (cursor < expression.Length)
        {
            AddLiteral(expression[cursor..]);
        }

        return parts.Count == 0 ? expression : string.Join("&", parts);

        void AddLiteral(string value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                parts.Add(Quote(value));
            }
        }
    }

    private static string? UsefulConditionTag(string? conditionTag)
    {
        if (string.IsNullOrWhiteSpace(conditionTag))
        {
            return null;
        }

        return conditionTag.StartsWith("Simple:", StringComparison.Ordinal) ? conditionTag : null;
    }

    private static IEnumerable<(ConnectFlowStep Step, ConnectFlowSequence? Incoming)> OrderedSteps(ConnectFlowProject project)
    {
        var stepsById = project.Steps
            .Where(s => !string.IsNullOrWhiteSpace(s.Id))
            .ToDictionary(s => s.Id!, StringComparer.Ordinal);
        var outgoing = project.Sequences
            .Where(s => !string.IsNullOrWhiteSpace(s.FromStepId) && !string.IsNullOrWhiteSpace(s.ToStepId))
            .GroupBy(s => s.FromStepId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => ParseId(s.Id)).ToArray(), StringComparer.Ordinal);

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var start = project.Steps.FirstOrDefault(s => string.Equals(s.StepType, "InitialStepItem", StringComparison.Ordinal))
            ?? project.Steps.FirstOrDefault();

        if (start is not null)
        {
            foreach (var entry in Walk(start, incoming: null))
            {
                yield return entry;
            }
        }

        foreach (var step in project.Steps.OrderBy(s => ParseId(s.Id)))
        {
            if (step.Id is not null && !visited.Contains(step.Id))
            {
                foreach (var entry in Walk(step, incoming: null))
                {
                    yield return entry;
                }
            }
        }

        IEnumerable<(ConnectFlowStep Step, ConnectFlowSequence? Incoming)> Walk(ConnectFlowStep step, ConnectFlowSequence? incoming)
        {
            if (step.Id is null || !visited.Add(step.Id))
            {
                yield break;
            }

            yield return (step, incoming);

            if (!outgoing.TryGetValue(step.Id, out var nextSequences))
            {
                yield break;
            }

            foreach (var sequence in nextSequences)
            {
                if (sequence.ToStepId is not null && stepsById.TryGetValue(sequence.ToStepId, out var next))
                {
                    foreach (var entry in Walk(next, sequence))
                    {
                        yield return entry;
                    }
                }
            }
        }
    }

    private static int ParseId(string? value) =>
        int.TryParse(value, out var id) ? id : int.MaxValue;

    private static string Metadata(ConnectFlowProject project, string key) =>
        project.Metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : "-";

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string QuoteJsonProperty(string value) => Quote(value);

    private static string BoolLiteral(bool value) => value ? "true" : "false";

    private static string IndentBlock(string value, int spaces)
    {
        var padding = new string(' ', spaces);
        return string.Join(Environment.NewLine, value.ReplaceLineEndings().Split(Environment.NewLine).Select(line => padding + line));
    }

    private static string SanitizeIdentifier(string value)
    {
        var sanitized = NonIdentifierCharacters().Replace(value, "_").Trim('_');
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "value";
        }

        return char.IsDigit(sanitized[0]) ? $"_{sanitized}" : sanitized;
    }

    [GeneratedRegex("[^A-Za-z0-9_]+")]
    private static partial Regex NonIdentifierCharacters();

    private sealed record HttpRequestDraft(
        string FunctionName,
        string Url,
        bool HasUnresolvedPath,
        string? ContentType,
        bool UseResponseEnvelope,
        bool IsSalesforce,
        bool UseSalesforcePublishResponse);

    private sealed record HttpHeaderDraft(string Name, string Value);

    private sealed record ResponseTypeDraft(ConnectFlowParameter? Parameter, bool UseSalesforceDefault);

    private sealed record DraftContext(
        IReadOnlyDictionary<string, string> PayloadWrapperByVariable,
        IReadOnlySet<string> ResponseEnvelopeVariables,
        ConnectorCatalog? ConnectorCatalog);

    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex ClassicExpressionSegments();
}
