---
title: LLM Chat Endpoints
description: Serve streaming LLM chat over your own conversation tables with fail-closed tenancy, SSE, and typed terminal contracts.
---

BifrostQL can host an LLM chat surface directly over two tables in your database. You declare a conversations/messages pair with `chat-*` metadata, opt in with `UseBifrostChat`, and BifrostQL exposes two HTTP endpoints: one to create conversations and one that appends the caller's message and streams the assistant completion back as [server-sent events](https://developer.mozilla.org/en-US/docs/Web/API/Server-sent_events).

Every read and write rides the same intent executors as GraphQL requests and protocol adapters, so tenant isolation, policy, soft delete, field encryption, and change history apply to chat traffic by construction — the endpoints have no SQL of their own and no way around the transformer pipelines. See [Chat over Your Tables](/concepts/chat/) for the concept and metadata contract.

## Declare the chat pair

```csharp
builder.Services.AddBifrostEndpoints(options =>
{
    options.AddEndpoint(endpoint =>
    {
        endpoint.ConnectionString = connectionString;
        endpoint.Path = "/graphql";
        endpoint.Metadata = new[]
        {
            "dbo.conversations { chat-conversations: enabled; chat-title: title; tenant-filter: tenant_id }",
            "dbo.messages { chat-messages: enabled; chat-role: role; chat-content: content; " +
                "chat-conversation-fk: conversation_id; chat-created-at: created_at; " +
                "tenant-filter: tenant_id }",
        };
    });
});
```

A minimal schema (SQL Server shown; any supported dialect works — the primary keys must be integer identities because the chat surface orders by them):

```sql
CREATE TABLE conversations (
    id        INT IDENTITY PRIMARY KEY,
    tenant_id NVARCHAR(64) NOT NULL,
    title     NVARCHAR(256) NULL
);

CREATE TABLE messages (
    id              INT IDENTITY PRIMARY KEY,
    tenant_id       NVARCHAR(64) NOT NULL,
    conversation_id INT NOT NULL REFERENCES conversations(id),
    role            NVARCHAR(16) NOT NULL,
    content         NVARCHAR(MAX) NULL,
    created_at      DATETIME2 NULL
);
```

## Enable the endpoints

```csharp
var app = builder.Build();

app.UseAuthentication();
app.UseBifrostChat(chat =>
{
    chat.Path = "/_chat";            // default
    chat.HistoryLimit = 50;          // default
    chat.SystemPrompt = "You answer questions about the ACME order database.";
    // chat.GraphQlEndpoint = "/graphql"; // required only with multiple endpoints
});
app.UseBifrostEndpoints();
```

`UseBifrostChat` validates its configuration at startup: it resolves the completion service eagerly, so a deployment with chat endpoints registered but no API key **fails at app start**, not on the first user request.

### Completion service configuration

The built-in provider is the Anthropic API, bound from the `BifrostQL:Chat` configuration section with `ANTHROPIC_API_KEY` as the api-key fallback:

| Setting | Default | Notes |
|---|---|---|
| `BifrostQL:Chat:ApiKey` | — | Falls back to the `ANTHROPIC_API_KEY` environment variable. Required. |
| `BifrostQL:Chat:Model` | `claude-opus-4-8` | Exact model id. |
| `BifrostQL:Chat:MaxTokens` | `64000` | Max output tokens per completion. |

Substitute your own provider by registering an `IChatCompletionService` before `AddBifrostEndpoints`/`AddBifrostQL`; the built-in registration is `TryAdd`.

## Endpoint contract

Both endpoints require an authenticated caller. Identity is projected through the same `IBifrostAuthContextFactory` as every other transport gate, fail-closed: an anonymous request is `401` **before the body is read or anything is touched**, and an OIDC token from an unmapped issuer is `403`.

### `POST {Path}/conversations`

Creates a conversation for the caller.

```json
// request (body optional)
{ "title": "Q3 revenue questions" }

// 200 response
{ "id": 42 }
```

Supplying a `title` requires a configured `chat-title` column; without one the mismatch is reported loudly (`500 configuration-error`), never silently dropped.

### `POST {Path}/conversations/{id}/messages`

Appends the caller's message and streams the completion. Request:

```json
{ "content": "Which customers churned last month?" }
```

The response is `text/event-stream`, flushed per event. Named events with JSON data:

| Event | Data | Meaning |
|---|---|---|
| `message-accepted` | `{ "userMessageId": 7, "conversationId": 42 }` | The user message is persisted; streaming begins. |
| `delta` | `{ "text": "…" }` | One incremental chunk of assistant text. |
| `done` | `{ "assistantMessageId": 8, "stopReason": "complete" \| "truncated" }` | Terminal success; the assistant message is persisted. |
| `error` | `{ "code": "…", "message": "…", … }` | Terminal failure; the stream ends after this event. |

Exactly one terminal event (`done` or `error`) ends every stream.

Error codes: `refusal` (with `refusalCategory` when the provider reported one), `provider-error` (with `retryable: true|false`), `not-found` (the conversation vanished mid-stream), and `internal-error` (details logged server-side, never leaked).

### HTTP status codes (before the stream starts)

| Status | Meaning |
|---|---|
| `400 invalid-request` | Malformed JSON, or missing/blank `content`. |
| `401 unauthenticated` | Anonymous caller — checked before anything else. |
| `403 denied` / `403 forbidden` | Fail-closed authorization denial (e.g. authenticated caller without the tenant claim a `tenant-filter` table requires) / unmapped OIDC issuer. |
| `404 not-found` | Conversation outside the caller's row scope. Cross-tenant and nonexistent are **indistinguishable by design**. |
| `405 method-not-allowed` | Anything but POST. |
| `409 stream-in-progress` | A completion is already streaming for this conversation. |

Once the SSE stream has started the HTTP status is committed to `200`; every later failure travels as a terminal `error` event instead.

## Semantics

### Refusal

When the model refuses (`stopReason: refusal` upstream), **nothing is persisted for the assistant** — including any partial text already streamed as deltas. The stream ends with `error {code: "refusal"}`. The caller's user message stays. Clients should discard the partial deltas they rendered.

### Truncation

When the completion hits the max-tokens ceiling, the accumulated text **is persisted as-is** and the stream ends with `done {stopReason: "truncated"}`. The stored message row carries only the content — the stop reason travels exclusively in the `done` event, so a client that needs to distinguish truncated turns must record it from the stream.

### Client disconnect

A dropped connection cancels the provider stream immediately. **No assistant row is written** — a partial answer is never persisted — but the accepted user message stays. Re-posting the question streams a fresh completion.

### Concurrency

One completion streams per conversation at a time; a second POST while one is streaming gets `409`, and nothing from the rejected request is persisted. The guard is **in-process** (a per-conversation key in the server's memory): in a multi-node deployment each node guards only its own streams, so route a conversation's requests to one node (sticky sessions) or add an external lock if you need a cluster-wide guarantee.

### History bounding

Each completion sends the conversation's **last `HistoryLimit` messages** (default 50) in chronological order, plus the configured `SystemPrompt` (never client input) first. Older messages are truncated from the oldest side — the model does not see them; they remain stored and pageable. Size the limit against your model's context window and cost budget.

## Security posture

- Every store operation runs the full filter/mutation transformer pipelines — the same tenant WHERE clauses and parameterized SQL the GraphQL path generates, verified by the chat endpoint conformance tests.
- Conversation visibility is probed **before** the per-conversation stream guard is taken, so an out-of-scope caller sees `404` and can never observe another tenant's streaming state through `409`.
- Message `role` values are a closed set (`user`, `assistant`, `system`); the store rejects anything else before writing.
- `created_at` is stamped server-side; the wire carries no caller-supplied timestamp.
