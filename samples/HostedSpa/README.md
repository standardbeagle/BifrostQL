# HostedSpa Sample

A minimal [Vite](https://vitejs.dev/) single-page application and a BifrostQL GraphQL
API served from **one ASP.NET process**. The SPA calls a same-origin `/graphql`
endpoint, so there is no CORS configuration to manage.

## What it shows

- `UseBifrostQL()` exposes the GraphQL endpoint at `/graphql` and a GraphiQL
  playground at `/playground`.
- `UseBifrostSpa()` serves the built SPA assets and adds an `index.html` route
  fallback that does not shadow `/graphql`, `/playground`, `/api`, or `/health`.
- A small SQLite database (`hostedspa-sample.db`) is created and seeded on first
  run, so no external database setup is required.

## Running it

The SPA is committed pre-built under `wwwroot/`, so the sample runs as-is:

```bash
dotnet run --project samples/HostedSpa
```

Then open the printed URL (for example `http://localhost:5000`):

- `/` — the SPA. Click **Load widgets from /graphql** to query the API.
- `/playground` — the GraphiQL playground.
- `/graphql` — the GraphQL endpoint (POST).

## Rebuilding the SPA

The SPA source lives under `spa/`. To change it and rebuild into `wwwroot/`:

```bash
cd samples/HostedSpa/spa
npm install
npm run build
```

For a SPA dev-server workflow with hot reload, run the ASP.NET host (`dotnet run`)
and `npm run dev` together — the Vite dev server proxies `/graphql` to the host.

## Project layout

| Path | Purpose |
|------|---------|
| `Program.cs` | ASP.NET host wiring `UseBifrostQL` + `UseBifrostSpa`. |
| `SampleDatabase.cs` | Creates and seeds the SQLite sample database on first run. |
| `appsettings.json` | BifrostQL configuration (SQLite provider, endpoint paths). |
| `spa/` | Vite SPA source. |
| `wwwroot/` | Pre-built SPA assets served by the host. |
