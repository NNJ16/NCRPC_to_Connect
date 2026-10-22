using System.Text;
using System.Text.RegularExpressions;

namespace NcrpToConnect.Cli;

internal static partial class FlowScriptDraftGenerator
{
    public static string Generate(NcrpProject project)
    {
        var builder = new StringBuilder();
        var moduleName = SanitizeIdentifier(project.ConnectorName ?? "Connector");
        var basePath = project.BasePath?.Trim('/') ?? string.Empty;

        AppendModuleHeader(builder, project, basePath);
        AppendStandardTypes(builder);
        AppendStandardHelpers(builder, basePath);

        foreach (var container in project.Containers)
        {
            AppendContainer(builder, project, container, []);
        }

        return builder.ToString();
    }

    private static void AppendModuleHeader(StringBuilder builder, NcrpProject project, string basePath)
    {
        builder.AppendLine("// FlowScript module generated from NCRP connector export");
        builder.AppendLine($"// Source: {project.SourcePath}");
        builder.AppendLine($"// Connector: {project.ConnectorName} ({project.ConnectorId})");
        builder.AppendLine($"// Base path: {ValueOrDash(project.BasePath)}");
        builder.AppendLine("//");
        builder.AppendLine("// Each connector operation is exposed as a callable function.");
        builder.AppendLine("// Adjust basePath, authentication, and types as needed.");
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(basePath))
        {
            builder.AppendLine($"let basePath = '{basePath}';");
        }
        else
        {
            builder.AppendLine("let basePath = '';");
        }

        builder.AppendLine();
        builder.AppendLine("open Seq;");
        builder.AppendLine();
    }

    private static void AppendStandardTypes(StringBuilder builder)
    {
        builder.AppendLine("type Header = {");
        builder.AppendLine("    Name: text,");
        builder.AppendLine("    Value: text");
        builder.AppendLine("};");
        builder.AppendLine();
    }

    private static void AppendStandardHelpers(StringBuilder builder, string basePath)
    {
        // HandleHeaders
        builder.AppendLine("private function HandleHeaders(headers: Header* = []): {} {");
        builder.AppendLine("    return Record.fromMembers(headers.map(h => Record.getMembers(h)).map(x => { label: str(x.value[0]), value: x.value[1] }));");
        builder.AppendLine("}");
        builder.AppendLine();

        // HandleHTTPResponse
        builder.AppendLine("private function HandleHTTPResponse(response: { statusCode: number, reasonPhrase: text, headers: { name: text, values: text* }*, contents: text}): text =>");
        builder.AppendLine("     case when response.statusCode >= 200 and response.statusCode < 300 then response.contents");
        builder.AppendLine("          else error response.reasonPhrase end;");
        builder.AppendLine();

        // addBasePath
        builder.AppendLine("private function addBasePath(url): text {");
        builder.AppendLine("    if url.left(1) = '/':");
        builder.AppendLine("        return `{basePath}{url}`;");
        builder.AppendLine("    else:");
        builder.AppendLine("        return `{basePath}/{url}`;");
        builder.AppendLine("}");
        builder.AppendLine();

        // GetResponse
        builder.AppendLine("private function GetResponse(url: text, headers: Header* = []): text =>");
        builder.AppendLine("    HandleHTTPResponse(HTTP.getText(addBasePath(url), HandleHeaders(headers)));");
        builder.AppendLine();

        // PostResponse
        builder.AppendLine("private function PostResponse(url: text, body: text, headers: Header* = []): text =>");
        builder.AppendLine("    HandleHTTPResponse(HTTP.postText(addBasePath(url), HandleHeaders(headers), body));");
        builder.AppendLine();

        // PatchResponse
        builder.AppendLine("private function PatchResponse(url: text, body: text, headers: Header* = []): text =>");
        builder.AppendLine("    HandleHTTPResponse(HTTP.patchText(addBasePath(url), HandleHeaders(headers), body));");
        builder.AppendLine();

        // PutResponse
        builder.AppendLine("private function PutResponse(url: text, body: text, headers: Header* = []): text =>");
        builder.AppendLine("    HandleHTTPResponse(HTTP.putText(addBasePath(url), HandleHeaders(headers), body));");
        builder.AppendLine();

        // DeleteResponse
        builder.AppendLine("private function DeleteResponse(url: text, headers: Header* = []): text {");
        builder.AppendLine("    let response = HTTP.delete(addBasePath(url), HandleHeaders(headers));");
        builder.AppendLine("    return case when response.statusCode >= 200 and response.statusCode < 300 then response.statusCode");
        builder.AppendLine("           else error response.reasonPhrase end;");
        builder.AppendLine("}");
        builder.AppendLine();
    }

    private static void AppendAuthorizationSetup(StringBuilder builder, IReadOnlyList<NcrpAuthorizationScheme> schemes)
    {
        if (schemes.Count == 0)
        {
            return;
        }

        builder.AppendLine("// ── Authorization ──────────────────────────────────────────────");

        // Query-string API key schemes — one plain text variable per key
        var querySchemes = schemes
            .Where(s =>
                (string.Equals(s.Kind, "ApiKeyAuthorization", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(s.Kind, "RestApiKeyAuthorizationDefinition", StringComparison.OrdinalIgnoreCase))
                && string.Equals(s.Location, "Query", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (querySchemes.Length > 0)
        {
            builder.AppendLine("// Query-string API keys — append these to the URL when calling operations.");
            builder.AppendLine("// TODO: Supply each value from environment or configuration.");
            foreach (var scheme in querySchemes)
            {
                var keyName = scheme.ApiKeyName ?? scheme.Name ?? "apikey";
                builder.AppendLine($"let {keyName}: text = \"\";");
            }
            builder.AppendLine();
        }

        // Header-based schemes — collect all into a single authHeaders list
        var headerEntries = new List<string>();
        foreach (var scheme in schemes)
        {
            if (querySchemes.Contains(scheme))
                continue;

            if (string.Equals(scheme.Kind, "ApiKeyAuthorization", StringComparison.OrdinalIgnoreCase)
                || string.Equals(scheme.Kind, "RestApiKeyAuthorizationDefinition", StringComparison.OrdinalIgnoreCase))
            {
                var keyName = scheme.ApiKeyName ?? "x-api-key";
                var location = scheme.Location ?? "Header";
                builder.AppendLine($"// Scheme: {scheme.Name} — Location: {location}, Key: {keyName}");
                headerEntries.Add($"{{ Name: \"{keyName}\", Value: \"\" }}");
            }
            else if (string.Equals(scheme.Kind, "OAuthAuthorization", StringComparison.OrdinalIgnoreCase)
                || string.Equals(scheme.Kind, "OAuth2Authorization", StringComparison.OrdinalIgnoreCase))
            {
                builder.AppendLine($"// Scheme: {scheme.Name} — Bearer token (OAuth)");
                headerEntries.Add("{ Name: \"Authorization\", Value: \"Bearer \" }");
            }
            else if (string.Equals(scheme.Kind, "BasicAuthorization", StringComparison.OrdinalIgnoreCase))
            {
                builder.AppendLine($"// Scheme: {scheme.Name} — Basic auth");
                headerEntries.Add("{ Name: \"Authorization\", Value: \"Basic \" }");
            }
            else
            {
                builder.AppendLine($"// Scheme: {scheme.Name} [{ValueOrDash(scheme.Kind)}] — TODO: configure authorization.");
                headerEntries.Add("{ Name: \"Authorization\", Value: \"\" }");
            }
        }

        if (headerEntries.Count > 0)
        {
            builder.AppendLine("// TODO: Supply header values from environment or configuration.");
            builder.AppendLine($"let authHeaders: Header* = [{string.Join(", ", headerEntries)}];");
        }

        builder.AppendLine();
    }

    private static void AppendContainer(StringBuilder builder, NcrpProject project, NcrpContainer container, IReadOnlyList<string> path)
    {
        var currentPath = path.Concat([container.Name ?? "Container"]).ToArray();
        var containerLabel = string.Join(" / ", currentPath);

        if (container.Operations.Count > 0)
        {
            builder.AppendLine($"// ── {containerLabel} ──────────────────────────────────────────────");
            builder.AppendLine();
        }

        foreach (var operation in container.Operations)
        {
            AppendOperation(builder, project, operation);
        }

        foreach (var child in container.Children)
        {
            AppendContainer(builder, project, child, currentPath);
        }
    }

    private static void AppendOperation(StringBuilder builder, NcrpProject project, NcrpOperation operation)
    {
        var functionName = BuildFunctionName(operation.Id ?? operation.DisplayName ?? "Operation");
        var httpMethod = operation.HttpMethod?.ToUpperInvariant() ?? "POST";

        var headerParams = operation.InputParameters.Where(IsHeaderParameter).ToArray();
        var pathParams = operation.InputParameters.Where(p => IsPathParameter(p, operation.Resource)).ToArray();
        var queryParams = operation.InputParameters.Where(p => IsQueryParameter(p, operation.Resource, httpMethod)).ToArray();
        var bodyParams = operation.InputParameters.Where(p => IsBodyParameter(p, operation.Resource, httpMethod)).ToArray();

        var hasBody = httpMethod is "POST" or "PUT" or "PATCH" && bodyParams.Length > 0;
        var hasHeaders = headerParams.Length > 0 || project.AuthorizationSchemes.Count > 0;

        var inputTypeName = $"{functionName}Input";
        var hasInputType = bodyParams.Length > 0;

        // Emit single nested input type if body exists
        if (hasInputType)
        {
            builder.AppendLine($"type {inputTypeName} = {FormatMembersStruct(bodyParams, indentLevel: 0)};");
            builder.AppendLine();
        }

        // Filter out "Nope", "None", and HTTP 4xx/5xx error responses from successful return types
        var validOutputParams = operation.OutputParameters
            .Where(p =>
            {
                if (p.HttpStatusCode.HasValue && p.HttpStatusCode.Value >= 400)
                {
                    return false;
                }

                if (string.Equals(p.ParameterType, "Nope", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.ParameterType, "None", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var effectiveName = p.EffectiveName;
                if (Regex.IsMatch(effectiveName, @"\b(4\d\d|5\d\d|Error|ErrorResponse|Response\s*4\d\d|Response\s*5\d\d)\b", RegexOptions.IgnoreCase))
                {
                    return false;
                }

                var isOkName = string.Equals(p.Name, "OK", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.DisplayName, "OK", StringComparison.OrdinalIgnoreCase);

                if (isOkName && p.EffectiveMembers.Count == 0 && (string.Equals(p.ParameterType, "String", StringComparison.OrdinalIgnoreCase) || string.Equals(p.ParameterType, "Object", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(p.ParameterType)))
                {
                    return false;
                }

                return true;
            })
            .ToArray();

        string returnType = "text";
        bool hasOutputType = false;
        string? outputTypeName = null;
        bool isArrayOutput = false;

        if (validOutputParams.Length > 0)
        {
            var p = validOutputParams.FirstOrDefault(op => op.HttpStatusCode == 200) ?? validOutputParams[0];

            isArrayOutput = p.IsArray
                || (p.ParameterType != null && p.ParameterType.Contains("Array", StringComparison.OrdinalIgnoreCase));

            if (p.EffectiveMembers.Count == 0 && IsPrimitiveType(p))
            {
                // Single primitive output (e.g. File, FileGallery, String, Number)
                var baseType = ResolveFlowType(p.ParameterType, p.ClassName);
                returnType = isArrayOutput ? $"{baseType}*" : baseType;
            }
            else
            {
                hasOutputType = true;
                outputTypeName = $"{functionName}Response";

                if (p.EffectiveMembers.Count > 0)
                {
                    builder.AppendLine($"type {outputTypeName} = {FormatMembersStruct(p.EffectiveMembers, indentLevel: 0)};");
                    builder.AppendLine();
                }
                else
                {
                    builder.AppendLine($"type {outputTypeName} = {FormatMembersStruct([p], indentLevel: 0)};");
                    builder.AppendLine();
                }

                returnType = isArrayOutput ? $"{outputTypeName}*" : outputTypeName;
            }
        }

        // Build function signature with pathParams, queryParams, hasInputType, hasHeaders
        var funcParams = BuildFunctionParameters(functionName, pathParams, queryParams, hasInputType, hasHeaders);

        builder.AppendLine($"/** {operation.DisplayName ?? operation.Id ?? "Operation"}: {httpMethod} {operation.Resource} */");
        builder.AppendLine($"function {functionName}({funcParams}): {returnType} {{");

        // Build URL
        var pathUrl = BuildUrlPath(operation.Resource, pathParams);
        builder.AppendLine($"    let url = `{pathUrl}`;");

        if (queryParams.Length > 0)
        {
            builder.AppendLine("    let queryParts: text* = [];");
            foreach (var q in queryParams)
            {
                var paramVar = SanitizeIdentifier(q.EffectiveName);
                var paramNameInUrl = !string.IsNullOrWhiteSpace(q.Name) ? q.Name : (!string.IsNullOrWhiteSpace(q.DisplayName) ? q.DisplayName : q.EffectiveName);
                builder.AppendLine($"    if {paramVar} != null: queryParts = queryParts & [`{paramNameInUrl}={{{paramVar}}}`];");
            }
            builder.AppendLine("    if queryParts.Count() > 0:");
            builder.AppendLine("        url = `{url}?{queryParts.Join(\"&\")}`;");
        }

        // Merge headers
        if (hasHeaders)
        {
            builder.AppendLine("    let allHeaders = headers;");
        }

        // Build body from the typed input record
        if (hasBody)
        {
            builder.AppendLine($"    let body = JSON.Serialize(input);");
        }

        // HTTP call using the helper functions
        var headersArg = hasHeaders ? ", allHeaders" : "";

        switch (httpMethod)
        {
            case "GET":
                builder.AppendLine($"    let response = GetResponse(url{headersArg});");
                break;
            case "DELETE":
                builder.AppendLine($"    let response = DeleteResponse(url{headersArg});");
                break;
            case "PATCH":
                builder.AppendLine($"    let response = PatchResponse(url, {(hasBody ? "body" : "\"\"")}{headersArg});");
                break;
            case "PUT":
                builder.AppendLine($"    let response = PutResponse(url, {(hasBody ? "body" : "\"\"")}{headersArg});");
                break;
            default: // POST
                builder.AppendLine($"    let response = PostResponse(url, {(hasBody ? "body" : "\"\"")}{headersArg});");
                break;
        }

        // Deserialize response
        if (hasOutputType && !string.IsNullOrEmpty(outputTypeName))
        {
            var targetDeserializeType = isArrayOutput ? $"{outputTypeName}*" : outputTypeName;
            builder.AppendLine($"    return JSON.deserialize<{targetDeserializeType}>(response);");
        }
        else
        {
            builder.AppendLine("    return response;");
        }

        builder.AppendLine("}");
        builder.AppendLine();
    }

    private static string FormatInlineType(NcrpParameter param, int indentLevel)
    {
        if (param.EffectiveMembers.Count > 0)
        {
            var structString = FormatMembersStruct(param.EffectiveMembers, indentLevel);
            return param.IsArray ? $"{structString}*" : (param.IsMandatory ? structString : $"{structString}?");
        }

        var baseType = ResolveFlowType(param.ParameterType, param.ClassName);
        var optional = param.IsMandatory ? "" : "?";
        return param.IsArray ? $"{baseType}*" : $"{baseType}{optional}";
    }

    private static string FormatMembersStruct(IReadOnlyList<NcrpParameter> members, int indentLevel)
    {
        if (members.Count == 0)
        {
            return "{}";
        }

        var sb = new StringBuilder();
        sb.AppendLine("{");

        var indent = new string(' ', indentLevel + 4);
        var closeIndent = new string(' ', indentLevel);

        for (var i = 0; i < members.Count; i++)
        {
            var m = members[i];
            var fieldName = SanitizeIdentifier(m.EffectiveName);
            var fieldType = FormatInlineType(m, indentLevel + 4);
            var comma = (i < members.Count - 1) ? "," : "";

            sb.AppendLine($"{indent}{fieldName}: {fieldType}{comma}");
        }

        sb.Append($"{closeIndent}}}");
        return sb.ToString();
    }

    private static bool IsPrimitiveType(NcrpParameter param)
    {
        var type = param.ParameterType ?? param.ClassName ?? string.Empty;
        if (string.Equals(type, "Object", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return param.EffectiveMembers.Count == 0;
    }

    private static string ToPascalCase(string input)
    {
        var sanitized = SanitizeIdentifier(input);
        var parts = sanitized.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            if (p.Length > 0)
            {
                sb.Append(char.ToUpperInvariant(p[0]));
                if (p.Length > 1) sb.Append(p[1..]);
            }
        }
        return sb.Length > 0 ? sb.ToString() : "Type";
    }

    private static string BuildFunctionParameters(
        string functionName,
        IReadOnlyList<NcrpParameter> pathParams,
        IReadOnlyList<NcrpParameter> queryParams,
        bool hasInputType,
        bool hasHeaders)
    {
        var parts = new List<string>();

        foreach (var p in pathParams)
        {
            var name = SanitizeIdentifier(p.EffectiveName);
            var type = ResolveFlowType(p.ParameterType, p.ClassName);
            var optional = p.IsMandatory ? "" : "?";
            parts.Add($"{name}: {type}{optional}");
        }

        foreach (var p in queryParams)
        {
            var name = SanitizeIdentifier(p.EffectiveName);
            var type = ResolveFlowType(p.ParameterType, p.ClassName);
            parts.Add($"{name}: {type}?");
        }

        if (hasInputType)
        {
            parts.Add($"input: {functionName}Input");
        }

        if (hasHeaders)
        {
            parts.Add("headers: Header* = []");
        }

        return string.Join(", ", parts);
    }

    private static string BuildUrlPath(string? resource, IReadOnlyList<NcrpParameter> pathParams)
    {
        if (string.IsNullOrWhiteSpace(resource))
        {
            return string.Empty;
        }

        var path = resource.StartsWith('/') ? resource : $"/{resource}";

        foreach (var p in pathParams)
        {
            var paramVar = SanitizeIdentifier(p.EffectiveName);
            var namesToCheck = new[] { p.Name, p.DisplayName, p.EffectiveName };
            foreach (var n in namesToCheck)
            {
                if (!string.IsNullOrWhiteSpace(n))
                {
                    path = Regex.Replace(path, $"\\{{{Regex.Escape(n)}\\}}", $"{{{paramVar}}}", RegexOptions.IgnoreCase);
                }
            }
        }

        return path;
    }

    private static bool IsHeaderParameter(NcrpParameter parameter) =>
        string.Equals(parameter.Kind, "HeaderParameter", StringComparison.OrdinalIgnoreCase)
        || string.Equals(parameter.Kind, "Header", StringComparison.OrdinalIgnoreCase);

    private static bool IsPathParameter(NcrpParameter parameter, string? resource)
    {
        if (string.Equals(parameter.Kind, "PathParameter", StringComparison.OrdinalIgnoreCase)
            || string.Equals(parameter.Kind, "PathParam", StringComparison.OrdinalIgnoreCase)
            || string.Equals(parameter.Kind, "Path", StringComparison.OrdinalIgnoreCase)
            || string.Equals(parameter.Kind, "UriParameter", StringComparison.OrdinalIgnoreCase)
            || string.Equals(parameter.Kind, "UrlParameter", StringComparison.OrdinalIgnoreCase)
            || string.Equals(parameter.Kind, "RouteParameter", StringComparison.OrdinalIgnoreCase)
            || string.Equals(parameter.Kind, "TemplateParameter", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(resource))
        {
            var namesToCheck = new[] { parameter.Name, parameter.DisplayName, parameter.EffectiveName };
            foreach (var n in namesToCheck)
            {
                if (!string.IsNullOrWhiteSpace(n) && resource.Contains($"{{{n}}}", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsQueryParameter(NcrpParameter parameter, string? resource, string httpMethod)
    {
        if (IsHeaderParameter(parameter) || IsPathParameter(parameter, resource))
        {
            return false;
        }

        if (string.Equals(parameter.Kind, "QueryParameter", StringComparison.OrdinalIgnoreCase)
            || string.Equals(parameter.Kind, "QueryParam", StringComparison.OrdinalIgnoreCase)
            || string.Equals(parameter.Kind, "Query", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var isDummyName = string.Equals(parameter.Name, "field", StringComparison.OrdinalIgnoreCase)
            || string.Equals(parameter.Name, "value", StringComparison.OrdinalIgnoreCase)
            || string.Equals(parameter.Name, "input", StringComparison.OrdinalIgnoreCase)
            || string.Equals(parameter.DisplayName, "field", StringComparison.OrdinalIgnoreCase);

        // On GET and DELETE requests, non-dummy parameters without body are query parameters
        if ((string.Equals(httpMethod, "GET", StringComparison.OrdinalIgnoreCase)
            || string.Equals(httpMethod, "DELETE", StringComparison.OrdinalIgnoreCase))
            && !isDummyName)
        {
            return true;
        }

        return false;
    }

    private static bool IsBodyParameter(NcrpParameter parameter, string? resource, string httpMethod) =>
        !IsHeaderParameter(parameter) && !IsPathParameter(parameter, resource) && !IsQueryParameter(parameter, resource, httpMethod);

    private static string ResolveFlowType(string? parameterType, string? className)
    {
        var type = parameterType ?? className ?? string.Empty;

        if (type.Contains("Boolean", StringComparison.OrdinalIgnoreCase) || type.Contains("bool", StringComparison.OrdinalIgnoreCase))
            return "boolean";

        if (type.Contains("DateTime", StringComparison.OrdinalIgnoreCase) || type.Contains("Date", StringComparison.OrdinalIgnoreCase))
            return "datetime";

        if (type.Contains("Int", StringComparison.OrdinalIgnoreCase)
            || type.Contains("Decimal", StringComparison.OrdinalIgnoreCase)
            || type.Contains("Double", StringComparison.OrdinalIgnoreCase)
            || type.Contains("Float", StringComparison.OrdinalIgnoreCase)
            || type.Contains("Single", StringComparison.OrdinalIgnoreCase)
            || type.Contains("Number", StringComparison.OrdinalIgnoreCase)
            || type.Contains("Long", StringComparison.OrdinalIgnoreCase)
            || type.Contains("Short", StringComparison.OrdinalIgnoreCase)
            || type.Contains("Byte", StringComparison.OrdinalIgnoreCase))
            return "number";

        if (string.Equals(type, "File", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "FileGallery", StringComparison.OrdinalIgnoreCase))
            return "binary";

        return "text";
    }

    private static readonly HashSet<string> FlowScriptKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "select", "where", "from", "in", "case", "when", "then", "else",
        "open", "let", "function", "type", "error", "not", "and", "or",
        "if", "is", "null", "true", "false", "return", "record", "seq", "http", "json",
        "as", "by", "to", "of", "with", "like", "order", "group", "having", "join", "on"
    };

    private static string BuildFunctionName(string operationName) =>
        SanitizeIdentifier(operationName);

    private static string SanitizeIdentifier(string value)
    {
        var sanitized = NonIdentifierCharacters().Replace(value, "_").Trim('_');
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "value";
        }

        if (char.IsDigit(sanitized[0]))
        {
            sanitized = $"_{sanitized}";
        }

        if (FlowScriptKeywords.Contains(sanitized))
        {
            return $"{sanitized}Param";
        }

        return sanitized;
    }

    private static string ValueOrDash(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    [GeneratedRegex("[^A-Za-z0-9_]+")]
    private static partial Regex NonIdentifierCharacters();
}
