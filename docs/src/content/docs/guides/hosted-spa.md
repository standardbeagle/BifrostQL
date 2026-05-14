---
title: Hosted SPA / API Mode
description: Serve a single-page app and a BifrostQL GraphQL API from one ASP.NET process — local dev proxy and production static hosting.
---

BifrostQL can host a single-page application (SPA) and a GraphQL API from a
**single ASP.NET process**. The SPA calls a same-origin `/graphql` endpoint, so
there is no CORS configuration to manage and no second service to deploy.

This guide covers the two workflows you need:

- **Local development** — a Vite dev server with hot reload, proxying `/graphql`
  to the ASP.NET host.
- **Production** — `UseBifrostSpa` serving the SPA's built static assets from the
  same process that serves GraphQL.

A complete, runnable example lives in [`samples/HostedSpa`](https://github.com/standardbeagle/BifrostQL/tree/main/samples/HostedSpa).

## Wiring up the host

`UseBifrostSpa` serves static SPA assets and adds an `index.html` route fallback
so client-side routes (for example `/widgets/42`) resolve to the SPA instead of
returning 404. Call it **after** `UseBifrostQL` so the GraphQL endpoint is
registered first and is not shadowed by the SPA fallback.

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBifrostQL(options =>
    options.BindStandardConfig(builder.Configuration));

var app = builder.Build();

// GraphQL endpoint first so the SPA fallback does not shadow it.
app.UseBifrostQL();

// Static SPA assets + index.html route fallback.
app.UseBifrostSpa();

await app.RunAsync();
```

By default `UseBifrostSpa` serves assets from the host's web root (`wwwroot`) and
excludes `/graphql`, `/api`, and `/health` from the `index.html` fallback. If you
expose the GraphQL playground or any other server route at a non-default path,
add it as an excluded prefix so the SPA fallback does not capture it:

```csharp
// The GraphQL playground lives at /playground in this sample.
app.UseBifrostSpa(spa => spa.AddExcludedPathPrefix("/playground"));
```

### `BifrostSpaOptions`

| Option | Default | Purpose |
|--------|---------|---------|
| `AssetDirectory` | host web root (`wwwroot`) | Directory holding the built SPA assets (the folder containing `index.html`). |
| `ExcludedPathPrefixes` | `/graphql`, `/api`, `/health` | Path prefixes that bypass the SPA `index.html` fallback. |
| `AddExcludedPathPrefix(prefix)` | — | Adds a prefix to `ExcludedPathPrefixes` — use for a non-default GraphQL endpoint or playground path. |

Prefix matching is case-insensitive and respects path-segment boundaries, so
`/api` excludes `/api/health` but not `/apixyz`.

## Local development: Vite dev server proxy

During development you want the Vite dev server (hot module reload, instant
rebuilds) for the SPA, while GraphQL requests still reach the ASP.NET host. Vite's
built-in proxy forwards `/graphql` to the host so the SPA keeps calling a
same-origin endpoint:

```js
// vite.config.js
import { defineConfig } from 'vite';

export default defineConfig({
  build: {
    // Build output goes straight into the ASP.NET host's wwwroot.
    outDir: '../wwwroot',
    emptyOutDir: true,
  },
  server: {
    proxy: {
      // `npm run dev` proxies /graphql to the ASP.NET host.
      '/graphql': 'http://localhost:5000',
    },
  },
});
```

Run both processes side by side:

```bash
# Terminal 1 — the ASP.NET host (serves /graphql).
dotnet run --project samples/HostedSpa

# Terminal 2 — the Vite dev server (serves the SPA with hot reload).
cd samples/HostedSpa/spa
npm install
npm run dev
```

Open the Vite dev server URL (typically `http://localhost:5173`). The SPA loads
from Vite; its `/graphql` calls are proxied to the host on port 5000. In this
workflow `UseBifrostSpa` is not serving the SPA — Vite is — but the host still
serves `/graphql`.

If the host listens on a different port, update both the proxy target in
`vite.config.js` and the `applicationUrl` in `Properties/launchSettings.json` so
they agree.

## Production: static hosting with `UseBifrostSpa`

In production there is no Vite dev server. You build the SPA to static assets and
let `UseBifrostSpa` serve them from the same process as GraphQL.

### 1. Build the SPA

```bash
cd samples/HostedSpa/spa
npm install
npm run build
```

With the `vite.config.js` above, this writes the built assets into the host's
`wwwroot`. If you build into a different directory, point `AssetDirectory` at it:

```csharp
app.UseBifrostSpa(spa =>
{
    spa.AssetDirectory = Path.Combine(builder.Environment.ContentRootPath, "spa-dist");
});
```

`UseBifrostSpa` throws at startup if `AssetDirectory` is set but the directory
does not exist, so a missing build fails fast rather than serving 404s.

### 2. Production-safe host configuration

The development sample uses `app.UseDeveloperExceptionPage()`. **Do not ship
that.** Guard developer-only middleware behind the environment check, and add the
hardening middleware that a public deployment needs:

```csharp
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    // Generic error page — never leak stack traces in production.
    app.UseExceptionHandler("/error");

    // Send HSTS so browsers pin HTTPS for this host.
    app.UseHsts();
}

// Redirect any plain-HTTP request to HTTPS.
app.UseHttpsRedirection();

app.UseBifrostQL();
app.UseBifrostSpa();

await app.RunAsync();
```

### 3. Static file cache headers

Vite emits content-hashed asset filenames (for example `index-a1b2c3d4.js`), so
those files can be cached aggressively and immutably. `index.html` must **not**
be cached — it is the entry point that references the current hashed assets.
Configure both with a custom `StaticFileOptions` and serve the SPA from your own
file provider:

```csharp
var spaRoot = Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
var spaFiles = new PhysicalFileProvider(spaRoot);

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = spaFiles,
    OnPrepareResponse = ctx =>
    {
        var path = ctx.File.Name;
        var headers = ctx.Context.Response.Headers;

        if (path.Equals("index.html", StringComparison.OrdinalIgnoreCase))
        {
            // The entry point must always be revalidated.
            headers.CacheControl = "no-cache, no-store, must-revalidate";
        }
        else
        {
            // Content-hashed assets never change under the same name.
            headers.CacheControl = "public, max-age=31536000, immutable";
        }
    },
});
```

When you need this level of control, call `UseStaticFiles` yourself with the
options above and rely on `UseBifrostSpa` only for the `index.html` route
fallback. Point `AssetDirectory` at the same `wwwroot` so the fallback serves the
same `index.html`.

### 4. HTTPS

Terminate TLS either at the ASP.NET host (Kestrel with a certificate) or at a
reverse proxy in front of it. In both cases:

- Keep `app.UseHttpsRedirection()` and `app.UseHsts()` (production branch above)
  so plain-HTTP requests are upgraded and browsers pin HTTPS.
- If TLS terminates at a reverse proxy, the host sees plain HTTP — see the
  forwarded-headers configuration below so redirects and HSTS still behave.

### 5. Reverse proxy

A reverse proxy (nginx, Apache, IIS, YARP, a cloud load balancer) commonly sits
in front of the host for TLS termination, compression, and load balancing.
Because the SPA and `/graphql` are the **same origin and same process**, the
proxy only needs a single upstream — no path-based split between an API service
and a static-site service.

Enable forwarded headers in the host so it reconstructs the original scheme and
client IP from the proxy:

```csharp
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
});
```

Call `UseForwardedHeaders` **early** — before `UseHttpsRedirection`, `UseHsts`,
`UseBifrostQL`, and `UseBifrostSpa` — so downstream middleware sees the corrected
scheme.

An nginx server block proxying everything to the host:

```nginx
server {
    listen 443 ssl;
    server_name app.example.com;

    ssl_certificate     /etc/ssl/app.example.com.crt;
    ssl_certificate_key /etc/ssl/app.example.com.key;

    location / {
        proxy_pass         http://127.0.0.1:5000;
        proxy_set_header   Host              $host;
        proxy_set_header   X-Real-IP         $remote_addr;
        proxy_set_header   X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
    }
}

server {
    # Redirect plain HTTP to HTTPS.
    listen 80;
    server_name app.example.com;
    return 301 https://$host$request_uri;
}
```

The SPA, the GraphQL endpoint, and the route fallback are all served from the
single `proxy_pass` upstream — no separate `location /graphql` block is required.

## Why same-origin

Serving the SPA and GraphQL from one origin avoids an entire class of deployment
friction:

- **No CORS** — the browser treats `/graphql` as same-origin, so there is no
  preflight configuration and no `Access-Control-*` headers to maintain.
- **One deployable** — a single process and a single reverse-proxy upstream
  instead of a static-site host plus a separate API service.
- **Consistent TLS and auth** — cookies, HSTS, and forwarded headers apply
  uniformly because there is only one origin.

## See also

- [`samples/HostedSpa`](https://github.com/standardbeagle/BifrostQL/tree/main/samples/HostedSpa) — a complete runnable example.
- [Configuration reference](/BifrostQL/reference/configuration/) — BifrostQL host configuration options.
- [Authentication](/BifrostQL/guides/authentication/) — securing the GraphQL endpoint.
