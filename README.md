# NCRP to Connect CLI PoC

Small .NET CLI proof-of-concept for reading Novacura connector export files, decoding the Base64 + gzip payload, parsing the XML, extracting connector inventory, reading Connect `.flow` exports, and generating first Flow Script / Flow Connect drafts.

## Run

Start the local upload interface:

```text
Start Ncrp To Connect.cmd
```

The interface accepts one or more Classic REST connector exports (`.ncrp`/`.ncrcp`) and one or more Connect flow exports (`.flow`/`.wap`), then shows REST-to-Flow-Script suggestions per step plus a complete draft.

```powershell
dotnet run --project src\NcrpToConnect.Cli\NcrpToConnect.Cli.csproj -- "Samples\Hubspot Flip Prod.ncrp"
```

Write decoded XML, structured JSON, and a Flow Script draft:

```powershell
dotnet run --project src\NcrpToConnect.Cli\NcrpToConnect.Cli.csproj -- "Samples\Hubspot Flip Prod.ncrp" --xml artifacts\hubspot.decoded.xml --json artifacts\hubspot.inventory.json --flow artifacts\hubspot.flow.txt
```

Read a Connect flow export and draft a Flow Connect translation:

```powershell
dotnet run --project src\NcrpToConnect.Cli\NcrpToConnect.Cli.csproj -- "Samples\Rejmes\TS Bilvision.flow" --xml artifacts\ts-bilvision.workflow.xml --json artifacts\ts-bilvision.flow-inventory.json --flow artifacts\ts-bilvision.flowconnect.txt
```

Read a Connect `.wap` package, match REST steps against `.ncrcp` connector exports in the same folder, and draft all included flows:

```powershell
dotnet run --project src\NcrpToConnect.Cli\NcrpToConnect.Cli.csproj -- "Samples\Rejmes\Linked - Book Test.wap" --json artifacts\linked-book-test.flow-package.json --flow artifacts\linked-book-test.flowconnect.txt
```

## Current extraction

- Connector metadata: id, name, description, base path.
- Authorization schemes: name, type, API key name, location.
- Categories and parent references.
- Operation container tree.
- REST operations: id, display name, description, method, resource, content type, flags.
- Input/output parameters: name, display name, kind, type, required flag, array flag, default value.
- Connect flow exports: metadata, embedded WorkflowData XML, step labels, sequence routes, user inputs, assignments, REST calls, database SQL, email tasks, scripts, and parameter mappings.
- Connect `.wap` packages: all embedded `.flow` entries are read and can be written as one combined Flow Connect draft.
- Classic REST machine steps are drafted as Connect Flow Script HTTP calls, including `HTTP.postText`/`HTTP.getText`, JSON body generation from Classic body mappings, content headers from connector metadata, generated response types, and response wrappers with `contents`, `statusCode`, `reasonPhrase`, and `headers` when an operation is resolved through a sibling `.ncrcp` export.

The generated Flow Script / Flow Connect output is intentionally a scaffold. It preserves operation names, HTTP shape, inputs, SQL, scripts, and flow order, then marks the exact places where target runtime syntax still needs to be mapped.
