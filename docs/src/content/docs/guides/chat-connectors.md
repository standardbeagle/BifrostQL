---
title: Chat Connectors
description: Expose tables to the chat LLM as Claude tools — explore reads, inline media, and human-gated plan writes — with metadata reference, caps, the confirmation protocol, and custom connector registration.
---

Chat connectors turn tables into **Claude tools the chat LLM can call
mid-conversation**. A table opts in with `chat-connector` metadata; the model
gets a generated tool per connector type, and every tool call executes under
the **caller's own auth context** through the intent executors — tenant
isolation, policy, encryption, and history apply exactly as on any other
transport. This page is the operator's reference: the metadata keys per
connector type, how tools are generated, the result caps, the confirmation
protocol for gated writes, vision caveats, and custom connector registration.

For the concept walk-through see
[Chat over Your Tables](/concepts/chat/#chat-connectors); for the HTTP/SSE
surface see the [LLM Chat Endpoints guide](/guides/llm-chat/). A runnable
demo with all three connector types lives in `examples/chat`.

## Connector metadata

```text
dbo.orders   { chat-connector: explore; chat-tool-description: Customer orders }

dbo.products { chat-connector: media; chat-media-column: Image;
               chat-media-caption: Caption }

dbo.publish_schedule { chat-connector: plan; chat-plan-operations: insert,update }
```

A connector table must be **published** (not `visibility: hidden`) and must
not be a change-history *target* (it may itself record history). Any number
of tables may be connectors, and one table may declare several types
(`chat-connector: explore,plan`). Validation **fails fast at model load** —
unknown tokens, media/plan keys without their type token, missing or
wrongly-typed columns, and unrecognized `chat-*` keys are all rejected before
the first chat request.

### All connector types

| Key | Values | Description |
|-----|--------|-------------|
| `chat-connector` | comma list of `explore` / `media` / `plan` | Opts the table in; unknown tokens and empty values are rejected |
| `chat-tool-description` | free text | Appended to the generated tool description — the schema author's channel for steering when the model calls the tool. Present-but-empty is rejected |

### `explore` — read/query

No extra keys. Any published table qualifies. The model can filter, sort,
page, and project the table's visible columns, read-only and capped
([caps](#result-caps)).

### `media` — serve an image/file column

| Key | Values | Description |
|-----|--------|-------------|
| `chat-media-column` | column name | Required. The column served. The **serving mode is derived from its type**: a binary column serves bytes through the auth-gated media route, a string column serves stored URLs verbatim |
| `chat-media-caption` | column name | Optional caption/alt-text column; must be string-typed |
| `chat-media-vision` | `enabled` | Lets the model view images itself (binary mode only — rejected on URL columns). See [vision caveats](#vision-cost-and-security-caveats) |

### `plan` — human-gated writes

| Key | Values | Description |
|-----|--------|-------------|
| `chat-plan-operations` | comma list of `insert` / `update` / `delete` | Required. The write allow-list. `delete` is **never implied** — it must be listed explicitly. The table must have a primary key |

## Tool generation

### Naming

Tool names derive from the table's **GraphQL name**, one prefix per
connector type:

| Connector | Tool name(s) |
|-----------|--------------|
| `explore` | `explore_<table>` |
| `media` | `media_<table>` |
| `plan` | `plan_insert_<table>`, `plan_update_<table>`, `plan_delete_<table>` — **one tool per allowed operation**; a disallowed operation's tool is absent from the schema entirely, not present-and-refused |

Tool names must be unique across all connectors; a collision fails fast at
startup with an error naming both connectors and the tool.

Each generated description is prescriptive about *when* to call the tool and
ends with your `chat-tool-description` text when present.

### Input schema derivation

The explore tool's input schema is derived from the table's columns:
`filters` (per-column, typed operators), `sort`, `limit`, `offset`, and a
`columns` projection whose values are enumerated column names. The media tool
shares the same lookup surface but has a **fixed result shape** (id, caption,
`mediaReference`) — no projection. Plan tools take `rows` shaped by the
table's writable columns.

Two column classes are treated specially, matching what execution enforces:

- **`visibility: hidden` columns are excluded from the tool entirely** — not
  in filters, not in sort, not in the projection enum, not in plan rows. The
  model cannot name them, and naming them anyway is a model-visible input
  error.
- **Encrypted columns cannot be predicates** — a filter or sort over an
  encrypted column would be a plaintext oracle, so they are omitted from the
  `filters`/`sort` schemas (and the read guard rejects them downstream
  regardless). They still **project**: values arrive decrypted or masked per
  the caller's roles, like any other read. The media content column itself is
  never a predicate either.

The model is an untrusted caller: every input is re-validated at execution,
and validation failures are fed back to the model as tool errors it can
recover from — never a crashed stream.

## Result caps

Tool results are fed back to the model verbatim, so every read is bounded.
All caps live on `ChatConnectorOptions`; a host overrides them by registering
its own instance **before** `AddBifrostEndpoints`/`AddBifrostQL`:

```csharp
builder.Services.AddSingleton(new ChatConnectorOptions
{
    ExploreRowCap = 25,
    PlanConfirmationTimeout = TimeSpan.FromMinutes(2),
});
```

| Option | Default | Behavior at the cap |
|--------|---------|---------------------|
| `ExploreRowCap` | 50 | Max rows per explore/media tool call; the payload notes the truncation — never silent |
| `ExplorePayloadCharCap` | 20 000 | Max characters per explore result; rows are dropped from the end until it fits, noted in the payload |
| `MediaVisionByteCap` | ~3.5 MB | Max raw image bytes attached as vision input (base64 expands 4/3 toward the provider's ~5 MB per-image limit); an over-cap image is a model-visible error |
| `PlanRowCap` | 20 | Max rows per plan proposal; over-cap is a model-visible error naming the cap, never a silent trim — a proposal must be exactly what the user confirms |
| `PlanConfirmationTimeout` | 5 minutes | How long a parked proposal waits before denying itself |

The tool **loop** is separately capped at `MaxToolIterations` model turns
(default 8, on the chat endpoint options); exceeding it ends the stream with
`error {code:"tool-loop-limit"}` and persists no assistant row.

## The confirmation protocol

A plan tool call **never writes**. It validates the model's rows and parks a
**write proposal**; the write happens only after the user approves it. The
full wire contract:

1. The stream emits a `confirmation` event —
   `{confirmationId, toolName, table, operation, rows, summary}` — and then
   **sits idle** (parked between provider turns; no upstream request is held
   open) until the proposal resolves. Size proxy/load-balancer idle timeouts
   against `PlanConfirmationTimeout`, or lower it.
2. The client renders the proposal and POSTs the decision to
   `POST {Path}/conversations/{id}/confirmations/{confirmationId}` with
   `{"approve": true}` or `{"approve": false, "reason": "…"}`. The reason is
   capped at **500 characters** (over-cap is a 400) and is persisted quoted,
   never raw — see [the endpoint contract](/guides/llm-chat/#plan-confirmations).
3. The stream emits `confirmation-resolved`
   (`{confirmationId, approved, reason}`) and resumes. An **approved**
   proposal executes through the batch mutation-intent seam under the
   original caller's identity — full transformer chain, one transaction, a
   veto on any row rolls the whole proposal back. A **denied** proposal
   writes nothing; the model receives the reason as a declined (non-error)
   tool result and can revise. A proposal nobody answers **denies itself**
   after `PlanConfirmationTimeout`.
4. Both outcomes are recorded in the conversation as a system-role
   transcript row before the stream resumes.

Fail-closed properties operators should know:

- **Single-use ids** — the confirmation id is cryptographically random and
  dies on first resolution (or with the stream that parked on it). An
  unknown, reused, cross-identity, or cross-conversation id answers the same
  `404`; a mismatched attempt does not consume the real proposal.
- **Per-node scope** — the confirmation registry is **in-process**, like the
  one-stream-per-conversation guard. In a multi-node deployment the
  confirmation POST must reach the node holding the parked stream: use
  sticky sessions.
- **Identity binding** — a proposal is bound to the caller's **tenant + user
  id** as projected into the auth context, and the resolving request must
  match both. Caveat: if your authentication stamps only a tenant claim and
  no per-user identifier, the binding **degrades to tenant-only** — any
  authenticated caller in the same tenant could resolve another user's
  proposal. Stamp a per-user claim (`sub`/name-identifier) so the binding is
  actually per-user. An auth context with neither tenant nor user fails
  fast — a write is never gated on an anonymous identity.

## Vision cost and security caveats

`chat-media-vision: enabled` gives the media tool a `view_image_id` input
that loads one row's image bytes (caller-scoped read, same row visibility as
any query) and attaches them to the tool result as base64. Before enabling
it, weigh:

- **Cost** — every viewed image rides the provider request as base64 (4/3 of
  the raw bytes, up to `MediaVisionByteCap` ≈ 3.5 MB raw per image) and is
  re-sent with the conversation on every subsequent turn of the tool loop.
  Image-heavy conversations get expensive quickly.
- **Prompt injection** — an image the model reads is **model input**. Text
  embedded in a stored image (a scanned document, a screenshot, a
  user-uploaded photo) can carry instructions the model may follow. Enable
  vision only on tables whose content you'd trust in the prompt itself, and
  keep plan operations gated (the confirmation protocol is the backstop: an
  injected instruction still cannot write without the user approving the
  proposal card).
- **Data egress** — with vision on, image bytes leave your infrastructure
  for the model provider. With vision off, `view_image_id` does not exist in
  the tool schema and media bytes never leave the server — clients fetch
  them through the auth-gated media route instead.

## Custom connectors

Implement `IChatConnector` and register it with `AddChatConnector<T>()`
(mirroring `AddFilterTransformer`). A connector owns both sides of its
tools: the definitions the model sees and the execution behind them.

```csharp
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.Chat;

public sealed class WeatherChatConnector : IChatConnector
{
    // 0-99 built-in, 100+ application connectors (ordering when tool sets build).
    public int Priority => 200;

    public IReadOnlyList<ChatToolDefinition> GetToolDefinitions(
        IDbModel model, ChatConnectorBinding binding)
    {
        // Called once per connector-marked table; return [] for bindings this
        // connector does not serve. Descriptions must say WHEN to call the tool
        // and should append binding.Config.ToolDescription when present.
        if (!string.Equals(binding.Table.GraphQlName, "locations", StringComparison.Ordinal))
            return Array.Empty<ChatToolDefinition>();

        return new[]
        {
            new ChatToolDefinition(
                "weather_lookup",
                "Call this when the user asks about current weather at a stored location.",
                """{"type":"object","properties":{"location_id":{"type":"integer"}},"required":["location_id"],"additionalProperties":false}"""),
        };
    }

    public async Task<ChatToolResult> ExecuteAsync(
        string toolName, string inputJson,
        IDictionary<string, object?> authContext, CancellationToken cancellationToken)
    {
        // inputJson is MODEL input — re-validate it. Throw ChatToolInputException
        // for feedback the model should read verbatim; every other exception is
        // sanitized to the exception TYPE name before it reaches the provider.
        var input = ParseAndValidate(inputJson);

        // Reads/writes must ride the intent executors with authContext so the
        // caller's tenant scope and policy apply — never an ambient identity.
        var forecast = await LookUpAsync(input, authContext, cancellationToken);
        return new ChatToolResult { TextPayload = forecast };
    }
}
```

```csharp
builder.Services.AddBifrostQL(options => options
    .AddChatConnector<WeatherChatConnector>());
```

Tool names must be unique across all connectors — a collision fails at
startup. Failures thrown from `ExecuteAsync` become `is_error` tool results
the model recovers from; only a `ChatToolInputException` message crosses to
the model verbatim, so author those messages as if they were public output.
