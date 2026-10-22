using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NcrpToConnect.Cli;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.Services.AddWindowsService();
builder.Host.UseWindowsService();
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 100 * 1024 * 1024);

var app = builder.Build();

app.MapGet("/", () => Results.Content(RenderPage(), "text/html; charset=utf-8"));

// ── REST API Endpoint: Returns raw FlowScript text from Classic REST Connector export (.ncrp) ──
app.MapPost("/api/convert", async (HttpRequest request, ILoggerFactory loggerFactory) =>
{
    var logger = loggerFactory.CreateLogger("NcrpToConnect.Web");
    if (!IsAuthorized(request, app.Configuration, logger))
    {
        return Results.Unauthorized();
    }

    var (project, error) = await ExtractSingleConnectorAsync(request);
    if (project is null)
    {
        return Results.BadRequest(error ?? "No valid .ncrp connector export provided.");
    }

    var flowScript = FlowScriptDraftGenerator.Generate(project);
    return Results.Content(flowScript, "text/plain; charset=utf-8");
}).DisableAntiforgery();

// ── REST API Endpoint: Returns JSON response from Classic REST Connector export (.ncrp) ──
app.MapPost("/api/convert/json", async (HttpRequest request, ILoggerFactory loggerFactory) =>
{
    var logger = loggerFactory.CreateLogger("NcrpToConnect.Web");
    if (!IsAuthorized(request, app.Configuration, logger))
    {
        return Results.Json(new { success = false, error = "Unauthorized: Missing or invalid API key." }, statusCode: 401);
    }

    var (project, error) = await ExtractSingleConnectorAsync(request);
    if (project is null)
    {
        return Results.BadRequest(new { success = false, error = error ?? "No valid .ncrp connector export provided." });
    }

    var flowScript = FlowScriptDraftGenerator.Generate(project);
    return Results.Json(new
    {
        success = true,
        connectorName = project.ConnectorName,
        connectorId = project.ConnectorId,
        basePath = project.BasePath,
        operationsCount = project.Operations.Count,
        flowScript
    });
}).DisableAntiforgery();

app.MapPost("/translate", async (HttpRequest request) =>
{
    var errors = new List<string>();
    var connectors = new List<NcrpProject>();
    var flows = new List<ConnectFlowProject>();

    try
    {
        var form = await request.ReadFormAsync();
        var connectorFiles = form.Files.GetFiles("connectors");
        var flowFiles = form.Files.GetFiles("flows");

        connectors.AddRange(await ReadConnectorsAsync(connectorFiles, errors));
        flows.AddRange(await ReadFlowsAsync(flowFiles, errors));
        var catalog = ConnectorCatalog.FromProjects(connectors);
        var result = BuildTranslationResult(connectors, flows, catalog);

        return Results.Content(RenderPage(new PageResult(
            connectorFiles.Count,
            connectors.Sum(connector => connector.Operations.Count),
            flowFiles.Count,
            flows.Count,
            flows.Sum(flow => flow.RestSteps.Count),
            result.Suggestions,
            result.FullDraft,
            errors)), "text/html; charset=utf-8");
    }
    catch (Exception ex)
    {
        errors.Add($"Translation Error: {ex.Message}");
        return Results.Content(RenderPage(new PageResult(0, 0, 0, 0, 0, [], "", errors)), "text/html; charset=utf-8");
    }
}).DisableAntiforgery();

app.Run();

static async Task<IReadOnlyList<NcrpProject>> ReadConnectorsAsync(IReadOnlyList<IFormFile> files, List<string> errors)
{
    var reader = new NcrpProjectReader();
    var projects = new List<NcrpProject>();

    foreach (var file in files)
    {
        try
        {
            await using var stream = file.OpenReadStream();
            using var textReader = new StreamReader(stream);
            projects.Add(reader.ReadContent(file.FileName, await textReader.ReadToEndAsync()));
        }
        catch (Exception ex)
        {
            errors.Add($"{file.FileName}: {ex.Message}");
        }
    }

    return projects;
}

static async Task<IReadOnlyList<ConnectFlowProject>> ReadFlowsAsync(IReadOnlyList<IFormFile> files, List<string> errors)
{
    var flowReader = new ConnectFlowReader();
    var packageReader = new ConnectFlowPackageReader();
    var flows = new List<ConnectFlowProject>();

    foreach (var file in files)
    {
        try
        {
            var extension = Path.GetExtension(file.FileName);
            await using var stream = file.OpenReadStream();

            if (string.Equals(extension, ".wap", StringComparison.OrdinalIgnoreCase))
            {
                flows.AddRange(packageReader.Read(stream, file.FileName).Entries.Select(entry => entry.Flow));
                continue;
            }

            using var textReader = new StreamReader(stream);
            flows.Add(flowReader.ReadXml(file.FileName, await textReader.ReadToEndAsync()));
        }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException or System.Xml.XmlException)
        {
            errors.Add($"{file.FileName}: {ex.Message}");
        }
    }

    return flows;
}

static TranslationResult BuildTranslationResult(IReadOnlyList<NcrpProject> connectors, IReadOnlyList<ConnectFlowProject> flows, ConnectorCatalog catalog)
{
    var fullDraft = new StringBuilder();
    var suggestions = new List<RestSuggestion>();

    if (flows.Count == 0 && connectors.Count > 0)
    {
        // No flow files — generate flowscript modules directly from connector definitions
        foreach (var connector in connectors)
        {
            var moduleDraft = FlowScriptDraftGenerator.Generate(connector);
            fullDraft.AppendLine(moduleDraft);
        }

        return new TranslationResult(suggestions, fullDraft.ToString());
    }

    foreach (var flow in flows)
    {
        var draft = FlowConnectDraftGenerator.Generate(flow, catalog);
        fullDraft.AppendLine(draft);

        foreach (var suggestion in ExtractRestSuggestions(flow, draft, catalog))
        {
            suggestions.Add(suggestion);
        }
    }

    return new TranslationResult(suggestions, fullDraft.ToString());
}

static IEnumerable<RestSuggestion> ExtractRestSuggestions(ConnectFlowProject flow, string draft, ConnectorCatalog catalog)
{
    var lines = draft.ReplaceLineEndings().Split(Environment.NewLine);
    for (var i = 0; i < lines.Length; i++)
    {
        if (!lines[i].TrimStart().StartsWith("# Step ", StringComparison.Ordinal))
        {
            continue;
        }

        var stepLine = lines[i].Trim();
        if (i + 1 >= lines.Length || !lines[i + 1].Contains("// Flow Designer generated code", StringComparison.Ordinal))
        {
            continue;
        }

        var block = new List<string> { lines[i], lines[i + 1] };
        var cursor = i + 2;
        while (cursor < lines.Length
            && !lines[cursor].TrimStart().StartsWith("# Step ", StringComparison.Ordinal)
            && !lines[cursor].TrimStart().StartsWith("# Route:", StringComparison.Ordinal)
            && !string.Equals(lines[cursor].Trim(), "end", StringComparison.Ordinal))
        {
            block.Add(lines[cursor]);
            cursor++;
        }

        var step = MatchStep(flow, stepLine);
        var operation = catalog.FindOperation(step?.ConnectorName, step?.OperationIdentifier ?? step?.OperationDisplayName);
        yield return new RestSuggestion(
            FlowName: flow.Name ?? Path.GetFileName(flow.SourcePath),
            StepTitle: step?.Label ?? step?.OperationDisplayName ?? stepLine,
            ConnectorName: step?.ConnectorName ?? "-",
            OperationName: step?.OperationIdentifier ?? step?.OperationDisplayName ?? "-",
            TargetVariable: step?.TargetVariableName ?? "-",
            Method: operation?.Operation.HttpMethod ?? ResolveMethodFromBlock(block),
            Url: operation?.Operation.Resource ?? ResolveUrlFromBlock(block),
            Code: string.Join(Environment.NewLine, block.Select(UnindentDraftLine)));
    }
}

static string UnindentDraftLine(string line)
{
    if (line.StartsWith("    ", StringComparison.Ordinal))
    {
        return line[4..];
    }

    return line.TrimStart();
}

static ConnectFlowStep? MatchStep(ConnectFlowProject flow, string stepLine)
{
    var marker = stepLine.StartsWith("# Step ", StringComparison.Ordinal) ? stepLine["# Step ".Length..] : stepLine;
    var idEnd = marker.IndexOf(':', StringComparison.Ordinal);
    if (idEnd < 0)
    {
        return null;
    }

    var id = marker[..idEnd].Trim();
    return flow.Steps.FirstOrDefault(step => string.Equals(step.Id, id, StringComparison.Ordinal));
}

static string ResolveMethodFromBlock(IEnumerable<string> block)
{
    var call = block.Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("let response = HTTP.", StringComparison.Ordinal));
    if (call is null)
    {
        return "-";
    }

    return call.Contains("getText", StringComparison.Ordinal) ? "GET"
        : call.Contains("deleteText", StringComparison.Ordinal) ? "DELETE"
        : call.Contains("patchText", StringComparison.Ordinal) ? "PATCH"
        : call.Contains("putText", StringComparison.Ordinal) ? "PUT"
        : "POST";
}

static string ResolveUrlFromBlock(IEnumerable<string> block)
{
    const string prefix = "let url = `";
    var urlLine = block.Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal));
    if (urlLine is null)
    {
        return "-";
    }

    var end = urlLine.IndexOf('`', prefix.Length);
    return end < 0 ? "-" : urlLine[prefix.Length..end];
}

static string RenderPage(PageResult? result = null)
{
    var body = result is null ? RenderEmptyState() : RenderResult(result);
    return $$"""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>NCRP to Connect</title>
  <style>
    :root {
      color-scheme: light;
      --bg: #f7f8fb;
      --text: #17202a;
      --muted: #5c6675;
      --line: #d8dde6;
      --panel: #ffffff;
      --accent: #126d73;
      --accent-strong: #0e555a;
      --accent-soft: #e2f3f2;
      --code: #101820;
      --warn: #8a4d12;
      --warn-bg: #fff4df;
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      font-family: Arial, Helvetica, sans-serif;
      background: var(--bg);
      color: var(--text);
    }
    main {
      width: min(1440px, calc(100vw - 32px));
      margin: 0 auto;
      padding: 24px 0 36px;
    }
    header {
      display: flex;
      align-items: end;
      justify-content: space-between;
      gap: 20px;
      margin-bottom: 18px;
    }
    h1 {
      margin: 0;
      font-size: 28px;
      font-weight: 720;
      letter-spacing: 0;
    }
    .subtitle {
      margin: 6px 0 0;
      color: var(--muted);
      font-size: 14px;
    }
    form {
      display: grid;
      grid-template-columns: 1fr 1fr auto;
      gap: 12px;
      align-items: end;
      padding: 14px;
      border: 1px solid var(--line);
      border-radius: 8px;
      background: var(--panel);
    }
    label {
      display: grid;
      gap: 7px;
      color: var(--muted);
      font-size: 13px;
      font-weight: 700;
    }
    input[type=file] {
      width: 100%;
      min-height: 42px;
      padding: 8px;
      border: 1px solid var(--line);
      border-radius: 6px;
      background: #fbfcfe;
      color: var(--text);
    }
    button {
      min-height: 42px;
      border: 1px solid var(--accent);
      border-radius: 6px;
      padding: 0 14px;
      background: var(--accent);
      color: white;
      font-weight: 700;
      cursor: pointer;
    }
    button.secondary {
      background: white;
      color: var(--accent-strong);
    }
    button:hover { background: var(--accent-strong); }
    button.secondary:hover { background: var(--accent-soft); }
    .stats {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      margin: 14px 0;
    }
    .stat {
      padding: 8px 10px;
      border: 1px solid var(--line);
      border-radius: 6px;
      background: var(--panel);
      color: var(--muted);
      font-size: 13px;
    }
    .stat strong { color: var(--text); }
    .layout {
      display: grid;
      grid-template-columns: minmax(0, 1fr) minmax(360px, 0.72fr);
      gap: 14px;
      align-items: start;
    }
    section {
      min-width: 0;
      border: 1px solid var(--line);
      border-radius: 8px;
      background: var(--panel);
    }
    .section-head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
      padding: 12px 14px;
      border-bottom: 1px solid var(--line);
    }
    h2 {
      margin: 0;
      font-size: 16px;
      letter-spacing: 0;
    }
    .suggestions {
      display: grid;
      gap: 10px;
      padding: 12px;
    }
    article {
      border: 1px solid var(--line);
      border-radius: 8px;
      overflow: hidden;
      background: #fff;
    }
    .item-head {
      display: grid;
      grid-template-columns: minmax(0, 1fr) auto;
      gap: 10px;
      padding: 10px 12px;
      border-bottom: 1px solid var(--line);
      background: #fbfcfe;
    }
    .item-title {
      margin: 0 0 4px;
      font-size: 14px;
      font-weight: 700;
    }
    .meta {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
      color: var(--muted);
      font-size: 12px;
    }
    .pill {
      max-width: 100%;
      padding: 4px 7px;
      border-radius: 999px;
      background: var(--accent-soft);
      color: var(--accent-strong);
      overflow-wrap: anywhere;
    }
    pre, textarea {
      width: 100%;
      margin: 0;
      border: 0;
      background: var(--code);
      color: #f2f5f7;
      font: 12px/1.55 Consolas, "Courier New", monospace;
      white-space: pre;
    }
    pre {
      overflow: auto;
      padding: 12px;
      max-height: 520px;
    }
    textarea {
      min-height: 720px;
      resize: vertical;
      padding: 12px;
      border-radius: 0 0 8px 8px;
    }
    .empty, .errors {
      margin-top: 14px;
      padding: 14px;
      border: 1px solid var(--line);
      border-radius: 8px;
      background: var(--panel);
      color: var(--muted);
    }
    .errors {
      border-color: #f2cc8f;
      background: var(--warn-bg);
      color: var(--warn);
    }
    @media (max-width: 900px) {
      main { width: min(100vw - 20px, 720px); padding-top: 14px; }
      header, form, .layout { grid-template-columns: 1fr; }
      form { display: grid; }
      button { width: 100%; }
    }
  </style>
</head>
<body>
  <main>
    <header>
      <div>
        <h1>NCRP to Connect</h1>
        <p class="subtitle">Classic REST connector + Connect flows → Flow Script suggestions.</p>
      </div>
    </header>
    <form method="post" action="/translate" enctype="multipart/form-data">
      <label>
        Classic REST connector export
        <input type="file" name="connectors" accept=".ncrp,.ncrcp" multiple required>
      </label>
      <label>
        Connect flow exports <span style="color:var(--muted);font-weight:400">(optional)</span>
        <input type="file" name="flows" accept=".flow,.wap" multiple>
      </label>
      <button type="submit">Analyze</button>
    </form>
    {{body}}
  </main>
  <script>
    async function copyBlock(id) {
      const el = document.getElementById(id);
      await navigator.clipboard.writeText(el.innerText);
    }
    function downloadDraft() {
      const text = document.getElementById('fullDraft').value;
      const blob = new Blob([text], { type: 'text/plain;charset=utf-8' });
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = 'flowconnect-draft.txt';
      link.click();
      URL.revokeObjectURL(url);
    }
  </script>
</body>
</html>
""";
}

static string RenderEmptyState() =>
    """
    <div class="empty">Choose one or more connector exports and flow exports to create the first suggestion.</div>
    """;

static string RenderResult(PageResult result)
{
    var html = new StringBuilder();
    html.AppendLine($"""
    <div class="stats">
      <div class="stat"><strong>{result.ConnectorFileCount}</strong> connector file(s)</div>
      <div class="stat"><strong>{result.ConnectorOperationCount}</strong> connector operation(s)</div>
      <div class="stat"><strong>{result.FlowFileCount}</strong> flow file(s)</div>
      <div class="stat"><strong>{result.FlowCount}</strong> flow(s)</div>
      <div class="stat"><strong>{result.RestStepCount}</strong> REST step(s)</div>
    </div>
    """);

    if (result.Errors.Count > 0)
    {
        html.AppendLine("<div class=\"errors\">");
        foreach (var error in result.Errors)
        {
            html.AppendLine($"<div>{Html(error)}</div>");
        }
        html.AppendLine("</div>");
    }

    html.AppendLine("""
    <div class="layout">
      <section>
        <div class="section-head">
          <h2>Flow Script Suggestions</h2>
        </div>
        <div class="suggestions">
    """);

    if (result.Suggestions.Count == 0)
    {
        html.AppendLine("<div class=\"empty\">No REST steps were found in the uploaded flows.</div>");
    }
    else
    {
        for (var i = 0; i < result.Suggestions.Count; i++)
        {
            var suggestion = result.Suggestions[i];
            var codeId = $"code-{i}";
            html.AppendLine($"""
          <article>
            <div class="item-head">
              <div>
                <p class="item-title">{Html(suggestion.StepTitle)}</p>
                <div class="meta">
                  <span class="pill">{Html(suggestion.FlowName)}</span>
                  <span class="pill">{Html(suggestion.ConnectorName)}.{Html(suggestion.OperationName)}</span>
                  <span class="pill">{Html(suggestion.Method)} {Html(suggestion.Url)}</span>
                  <span class="pill">→ {Html(suggestion.TargetVariable)}</span>
                </div>
              </div>
              <button class="secondary" type="button" onclick="copyBlock('{codeId}')">Copy</button>
            </div>
            <pre id="{codeId}">{Html(suggestion.Code)}</pre>
          </article>
""");
        }
    }

    html.AppendLine($"""
        </div>
      </section>
      <section>
        <div class="section-head">
          <h2>Complete Draft</h2>
          <button class="secondary" type="button" onclick="downloadDraft()">Download</button>
        </div>
        <textarea id="fullDraft" spellcheck="false">{Html(result.FullDraft)}</textarea>
      </section>
    </div>
    """);

    return html.ToString();
}

static string Html(string value) => HtmlEncoder.Default.Encode(value);

static bool IsAuthorized(HttpRequest request, IConfiguration configuration, ILogger? logger = null)
{
    var rawConfiguredKey = configuration["ApiKey"] ?? Environment.GetEnvironmentVariable("API_KEY");
    if (string.IsNullOrWhiteSpace(rawConfiguredKey))
    {
        return true;
    }

    var configuredKey = CleanKey(rawConfiguredKey);

    // 1. Check custom headers (x-api-key, api-key, apikey, x-apikey, ApiKey)
    string[] headerNames = ["x-api-key", "api-key", "apikey", "x-apikey", "ApiKey"];
    foreach (var headerName in headerNames)
    {
        if (request.Headers.TryGetValue(headerName, out var headerValues))
        {
            foreach (var val in headerValues)
            {
                if (!string.IsNullOrWhiteSpace(val) && string.Equals(CleanKey(val!), configuredKey, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
    }

    // 2. Check Authorization header: "Bearer <key>", "ApiKey <key>", "Key <key>", or direct "<key>"
    if (request.Headers.TryGetValue("Authorization", out var authHeaders))
    {
        foreach (var authVal in authHeaders)
        {
            if (string.IsNullOrWhiteSpace(authVal)) continue;
            var token = authVal.Trim();
            if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = token["Bearer ".Length..].Trim();
            }
            else if (token.StartsWith("ApiKey ", StringComparison.OrdinalIgnoreCase))
            {
                token = token["ApiKey ".Length..].Trim();
            }
            else if (token.StartsWith("Key ", StringComparison.OrdinalIgnoreCase))
            {
                token = token["Key ".Length..].Trim();
            }

            if (string.Equals(CleanKey(token), configuredKey, StringComparison.Ordinal))
            {
                return true;
            }
        }
    }

    // 3. Check query parameters (apiKey, apikey, api_key, key, access_token)
    string[] queryNames = ["apiKey", "apikey", "api_key", "key", "access_token"];
    foreach (var queryName in queryNames)
    {
        if (request.Query.TryGetValue(queryName, out var queryValues))
        {
            foreach (var val in queryValues)
            {
                if (!string.IsNullOrWhiteSpace(val) && string.Equals(CleanKey(val!), configuredKey, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
    }

    logger?.LogWarning("Unauthorized API request to {Path}. Headers present: [{Headers}]. Configured key: '{ConfiguredKey}'",
        request.Path,
        string.Join(", ", request.Headers.Select(h => $"{h.Key}={h.Value}")),
        configuredKey);

    return false;
}

static string CleanKey(string key) => key.Trim().Trim('"').Trim('\'');

static async Task<(NcrpProject? Project, string? Error)> ExtractSingleConnectorAsync(HttpRequest request)
{
    var reader = new NcrpProjectReader();
    string base64Content = string.Empty;
    string fileName = "connector.ncrp";

    if (request.HasFormContentType)
    {
        var form = await request.ReadFormAsync();
        var file = form.Files.GetFile("file") ?? form.Files.GetFile("connector") ?? form.Files.FirstOrDefault();
        if (file is null)
        {
            return (null, "No file uploaded in form field 'file' or 'connector'.");
        }

        fileName = file.FileName;
        await using var stream = file.OpenReadStream();
        using var textReader = new StreamReader(stream);
        base64Content = await textReader.ReadToEndAsync();
    }
    else if (request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true)
    {
        var payload = await request.ReadFromJsonAsync<ConvertRequestPayload>();
        base64Content = payload?.Content ?? payload?.Base64Content ?? string.Empty;
    }
    else
    {
        using var textReader = new StreamReader(request.Body);
        base64Content = await textReader.ReadToEndAsync();
    }

    if (string.IsNullOrWhiteSpace(base64Content))
    {
        return (null, "Provided connector payload is empty.");
    }

    try
    {
        var project = reader.ReadContent(fileName, base64Content.Trim());
        return (project, null);
    }
    catch (Exception ex)
    {
        return (null, $"Error parsing .ncrp file: {ex.Message}");
    }
}

internal record ConvertRequestPayload(string? Content, string? Base64Content);

internal sealed record PageResult(
    int ConnectorFileCount,
    int ConnectorOperationCount,
    int FlowFileCount,
    int FlowCount,
    int RestStepCount,
    IReadOnlyList<RestSuggestion> Suggestions,
    string FullDraft,
    IReadOnlyList<string> Errors);

internal sealed record TranslationResult(
    IReadOnlyList<RestSuggestion> Suggestions,
    string FullDraft);

internal sealed record RestSuggestion(
    string FlowName,
    string StepTitle,
    string ConnectorName,
    string OperationName,
    string TargetVariable,
    string Method,
    string Url,
    string Code);
