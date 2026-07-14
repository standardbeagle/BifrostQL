---
title: "RESP (Redis) Smoke Runbook"
description: "Manually verify that redis-cli and a real StackExchange.Redis app can connect to the BifrostQL Redis wire-protocol endpoint over a real network socket, read rows with GET/HGETALL/SCAN, and stay tenant-scoped — the end-to-end path the automated tests exercise in-process."
---

BifrostQL exposes a Redis RESP wire-protocol front door so any tool that speaks
RESP — `redis-cli`, StackExchange.Redis, any Redis client library — can read your
tables as key-addressed rows (`<table>:<pk>`). Reads travel the same transformer
pipeline as GraphQL, so tenant isolation, soft-delete, and policy read guards are
enforced on the wire.

Unlike the pgwire BI-tool smoke, the RESP StackExchange.Redis smoke **is
automated and in the gate**: `RespStackExchangeSmokeTests` connects a **real**
`ConnectionMultiplexer` from the StackExchange.Redis NuGet package to an
in-process loopback RESP endpoint and reads via GET/HGETALL/SCAN. That is a
genuine real-client smoke (a real client library over a real TCP socket), needs
no external service, and runs headless in CI.

This runbook is the **manual cross-process** complement: point a `redis-cli`
binary or a standalone StackExchange.Redis app at a **running** BifrostQL host.

> **Honesty note.** The automated suite proves a real StackExchange.Redis client
> completes the handshake (HELLO/AUTH/CLIENT/binary-ECHO tracer) and reads
> tenant-scoped data in-process. It does **not** prove your specific `redis-cli`
> build or deployed host wiring works cross-process — that is what this runbook is
> for. If you have not run the steps below, do not claim the cross-process smoke
> passed.

## Prerequisites

- A BifrostQL host with the RESP front door enabled (`AddBifrostResp`) and a
  configured `IRespCredentialStore` login whose principal carries a tenant claim.
- The endpoint reachable on a host/port, e.g. `localhost:6379`.
- `redis-cli` and/or a small .NET app referencing StackExchange.Redis.

Example server wiring:

```csharp
builder.Services.AddBifrostResp(o =>
{
    o.Port = 6379;
    o.RequireAuthentication = true;   // AUTH required before any identity-bearing command
    o.Endpoint = "/graphql";          // the BifrostQL endpoint to read
    // o.EnableWrites = true;         // opt-in: SET/HSET/DEL through the mutation pipeline
});
// register your IRespCredentialStore (login -> principal) separately
```

> **No STARTTLS.** RESP has no in-protocol TLS. `AUTH` crosses the wire in the
> clear unless TLS is terminated at the listener or a proxy — the same
> operational contract as a real Redis. Do not send real secrets over an
> untrusted network without TLS termination.

## Step 1 — redis-cli (fastest sanity check)

```bash
redis-cli -h localhost -p 6379 --user <login> --pass <secret>
```

At the prompt:

```
PING                          -- +PONG (connection is live)
HGETALL <table>:<pk>          -- the row's visible columns as a field/value hash
GET <table>:<pk>              -- the row as a JSON string
SCAN 0 MATCH <table>:* COUNT 100   -- enumerate ONLY your tenant's keys
LPUSH x y                     -- (ERR unknown command …) clean error, connection stays live
```

**Expected:** `HGETALL`/`GET` return only rows for the tenant your login maps to;
a key in another tenant returns an empty hash / nil (indistinguishable from
missing — no leak); `SCAN` lists only your tenant's keys. Run the same commands
as a login in a **different** tenant and confirm the key/row sets are disjoint.
An unsupported command (`LPUSH`, `SUBSCRIBE`, `MULTI`) returns a clean
`ERR unknown command …` and the connection stays usable.

## Step 2 — a real StackExchange.Redis app

```csharp
var config = new ConfigurationOptions
{
    EndPoints = { { "localhost", 6379 } },
    User = "<login>",
    Password = "<secret>",
    AbortOnConnectFail = false,
};
await using var mux = await ConnectionMultiplexer.ConnectAsync(config);
var db = mux.GetDatabase();

Console.WriteLine(db.Ping());                              // round-trips
foreach (var e in await db.HashGetAllAsync("<table>:<pk>"))// your tenant's row
    Console.WriteLine($"{e.Name}={e.Value}");
var scan = (RedisResult[])await db.ExecuteAsync("SCAN", "0", "MATCH", "<table>:*", "COUNT", "100");
```

**Expected:** `ConnectionMultiplexer.ConnectAsync` returns a connected
multiplexer (the HELLO/AUTH/CLIENT/ECHO-tracer handshake all answer); `HGETALL`
and `SCAN` return only your tenant's data.

## What to record

For a release smoke, note: redis-cli connected (y/n); StackExchange.Redis
connected (y/n); GET/HGETALL/SCAN returned data (y/n); tenant isolation confirmed
with a second login (y/n); an unsupported command returned a clean `-ERR` without
hanging (y/n). Anything that fails is a real defect — do not paper over it.

## Troubleshooting

- **A StackExchange.Redis client never connects (times out on the tracer):** the
  client's connection tracer is a **binary** `ECHO`; the front door must echo the
  raw bytes verbatim (it does). If a fork/older build changed ECHO to a
  string round-trip, the bulk length changes and the client desyncs — restore
  binary-safe ECHO.
- **`CONFIG GET` / `CLUSTER` / `SENTINEL` errors during connect:** expected. The
  front door does not implement Redis admin/cluster commands; StackExchange.Redis
  tolerates the `-ERR` and connects anyway.
- **A read errors with a generic `ERR internal error`:** by design the wire
  sanitizes execution faults (tenant-context-required, policy-read-deny); check
  the server logs for the real cause.
- **Writes return `ERR write commands are disabled`:** the write surface is
  off by default; set `RespWireOptions.EnableWrites = true` to opt in
  (SET/HSET/DEL then route through the mutation transformer pipeline).
```
