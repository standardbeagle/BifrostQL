# BifrostQL.UI.Tests

Tests for the `bifrostui` desktop application: its HTTP API surface, the
Photino message bridge, credential/vault handling, and the schema projections
the frontend consumes.

## Running

```bash
dotnet test tests/BifrostQL.UI.Tests/BifrostQL.UI.Tests.csproj
```

These run in the epic tier, so they execute on the current TFM only. Nothing
here needs a live SQL Server: quickstart coverage uses SQLite, and the
connection tests assert on failure handling rather than on a reachable server.

## What each file covers

| File | Covers |
|------|--------|
| `BifrostUIApiTests.cs` | HTTP endpoints — health, connection test, database listing, static file serving |
| `QuickStartEndToEndTests.cs` | SQLite quickstart creation through to a queryable schema |
| `BridgeDispatcherTests.cs` | Photino message bridge dispatch and error propagation |
| `VisualQueryBridgeTests.cs` | Visual query designer bridge contract |
| `BuilderSchemaProjectionTests.cs` | Schema projected into the query-builder shape |
| `SoftDeleteShapeTests.cs` | Soft-delete metadata surfaced to the client |
| `PolymorphicLeakRepro.cs` | Regression guard against a polymorphic serialization leak |
| `VaultStoreTests.cs`, `VaultServerProviderTests.cs` | Saved vault entries and provider resolution |
| `CredentialPromptHtmlTests.cs`, `CredentialResultTests.cs` | Browser credential prompt rendering and result parsing |
| `SshTunnelManagerTests.cs` | SSH tunnel lifecycle |
| `SampleConfigTests.cs` | Shipped sample configuration stays loadable |
| `HeadlessUiServer.cs` | Shared harness that boots the UI host headlessly for API tests |

## API surface under test

Defined in `src/BifrostQL.UI/Web/` (`ConnectionEndpoints.cs`,
`MetadataEndpoints.cs`):

- `GET /api/health`
- `POST /api/connection/test` — validate a connection without persisting it
- `POST /api/databases` — list databases on a server
- `POST /api/database/create-quickstart` — create a SQLite quickstart database

`POST /api/database/create` remains routed but deliberately returns an error:
it accepted password-bearing connection strings over HTTP. Use the quickstart
endpoint, or save a vault entry and connect via `/api/vault/connect`. The
`/api/connection/set` endpoint was removed for the same reason; see the note in
`src/BifrostQL.UI/Web/ApiRequests.cs`.

## Launching the app by hand

```bash
./bifrostui                       # no connection string — opens the welcome UI
./bifrostui "Server=...;Database=...;"   # connect on launch
./bifrostui --headless            # server only, no desktop window
./bifrostui --headless -p 8080    # choose the port
```

Frontend sources live in `src/BifrostQL.UI/frontend`; `src/BifrostQL.UI/wwwroot`
is Vite build output and is not edited by hand.
