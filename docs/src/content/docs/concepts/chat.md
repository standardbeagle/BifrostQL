---
title: Chat over Your Tables
description: Declare a chat schema — conversations and messages — over tables you already own, with tenant isolation, field encryption, and change history composing like on any other table.
---

The **chat module** publishes a chat schema over **user-supplied tables**: you bring a
conversations table and a messages table (any names, any extra columns), and metadata
maps them onto the roles the chat surface needs. Because the tables are ordinary
published tables you own, the rest of BifrostQL composes with them unchanged —
`tenant-filter` scopes conversations per tenant, `encrypt` protects message content at
rest, and `history` records a change trail of the chat itself.

:::note
This page covers the **metadata contract and fail-fast validation**. The HTTP surface
over it — streaming chat endpoints with SSE — is covered in the
[LLM Chat Endpoints guide](/guides/llm-chat/).
:::

## Metadata

Exactly **one conversations table paired with one messages table** per model:

```text
dbo.conversations {
  chat-conversations: enabled;
  chat-title: Title
}

dbo.messages {
  chat-messages: enabled;
  chat-role: Role;
  chat-content: Content;
  chat-conversation-fk: ConversationId;
  chat-created-at: CreatedAt
}
```

| Key | Table | Description |
|-----|-------|-------------|
| `chat-conversations` | conversations | Opt-in marker; the only valid value is `enabled` |
| `chat-title` | conversations | Optional title column |
| `chat-messages` | messages | Opt-in marker; the only valid value is `enabled` |
| `chat-role` | messages | Role column (`user`/`assistant`/...); string-typed |
| `chat-content` | messages | Message body column; string-typed |
| `chat-conversation-fk` | messages | Column referencing the conversations table's primary key |
| `chat-created-at` | messages | Message timestamp column; date/time-typed |

## Validation

The configuration fails fast at model load — never silently at request time:

- Unknown `chat-*` keys and unknown opt-in tokens are rejected; present-but-empty
  values are errors, not omissions.
- The messages mapping is all-or-nothing: `chat-role`, `chat-content`,
  `chat-conversation-fk`, and `chat-created-at` are required together.
- Exactly one conversations table and one messages table — and never one without
  the other.
- Every mapped column must exist on its table; role/content must be string-typed and
  created-at date/time-typed.
- Both chat tables need a **single-column, integer-typed primary key** (composite and
  non-integer keys — GUIDs included — are rejected with a clear error). This is an
  ordering contract: conversations list newest first by key descending and message
  paging breaks created-at ties by key ascending, which is only creation order for a
  monotonic integer identity key. `chat-conversation-fk` must actually reference the
  conversations key through a declared **single-column** foreign key or an explicit
  `join` metadata rule; a composite foreign key that merely includes the column is
  rejected by name.
- Both chat tables must be **published** (not `visibility: hidden`) and must not be
  change-history **targets**. They *may* be history-**enabled** — recording the chat's
  own change trail composes.

## Chat connectors

Beyond the conversation store, any table can be exposed **to the chat LLM as a
Claude tool** by declaring connector types on it:

```text
dbo.orders { chat-connector: explore,plan; chat-plan-operations: insert,update }

dbo.documents {
  chat-connector: media;
  chat-media-column: Image;
  chat-media-vision: enabled;
  chat-media-caption: Caption;
  chat-tool-description: Scanned contract documents
}
```

- **`explore`** — the model can read and query the table. Any published table
  qualifies; no extra columns needed. `visibility: hidden` columns are excluded
  from the tool entirely, encrypted columns cannot be filter/sort predicates
  (their values still project, decrypted or masked per the caller's roles), and
  results are row- and payload-capped with explicit truncation notes.
- **`media`** — the model can look up an image/file column (`chat-media-column`)
  and hand out references to it. The serving mode is derived from the column type
  — a binary column serves bytes through the auth-gated media route, a string
  column serves stored URLs — and `chat-media-vision: enabled` (binary mode only)
  lets the model view images itself. See [Media tools](#media-tools) below.
- **`plan`** — the model can propose gated writes, limited to the
  `chat-plan-operations` allow-list. `delete` is never implied; it must be listed
  explicitly, and the table must have a primary key.

Connector configuration fails fast at model load with the same rigor as the chat
pair: unknown tokens, stray media/plan keys without their type token, missing or
wrongly-typed columns, and unpublished or history-target tables are all rejected.
The chat pair tables themselves **may** be connectors — "explore my own
conversation history" is a legitimate tool, and the row-scope transformers guard
connector reads exactly as they guard the chat store's. Note the scope that
implies: an explore tool on the **messages table is tenant-scoped, not
conversation-scoped** — the model can read messages from any conversation within
the caller's tenant, by design. If a chat surface must not see sibling
conversations, do not mark the messages table `explore`.

Custom connectors implement `IChatConnector` and register with
`AddChatConnector<T>()` (mirroring `AddFilterTransformer`). A connector owns both
the tool definitions the model sees — prescriptive *when-to-call* descriptions,
incorporating `chat-tool-description` when present — and the execution behind
them. Tool names must be unique across all connectors; a collision fails fast
with an error naming both connectors and the tool.

### The tool-use loop

When any connector exposes tools, the completion becomes a **multi-turn tool
loop** inside the completion service:

1. The request carries every connector tool. When the model stops to call tools,
   **all** tool calls of the turn execute (parallel-safe) under the **caller's
   own auth context** — reads and writes ride the intent executors with tenant
   isolation, policy, and the rest of the transformer pipeline applied, exactly
   like any other transport. There is no ambient identity.
2. All results return to the model in one message (the Anthropic `tool_result`
   contract) and the loop continues until the model finishes its answer.
3. A connector failure is fed back to the model as an `is_error` tool result —
   the model sees the error text and recovers; the stream itself never crashes on
   a tool fault.
4. The loop is capped at `MaxToolIterations` model turns (default **8**,
   configurable on the chat endpoint options). Exceeding the cap is a **typed
   error** — the SSE stream ends with `error {code:"tool-loop-limit"}` and **no
   assistant row is persisted**.

Tool activity streams live as SSE `tool` events (`{name, phase, summary}`,
`phase` ∈ `call`/`result`) interleaved with the text deltas, so clients can show
progress. The persistence contract is unchanged by tools: the assistant row is
the **final answer text only** — the tool transcript is streamed, not stored.

### Media tools

A `chat-connector: media` table becomes one `media_<table>` lookup tool. It
shares the explore tool's `filters`/`sort`/`limit`/`offset` input surface (same
hidden-column and encrypted-predicate rules; the media content column itself is
never a predicate) but its result shape is **fixed** — no columns projection:

```json
{"rows": [{"id": 7, "caption": "Contract page 1",
           "mediaReference": "bifrost-media://documents/7"}]}
```

The `mediaReference` depends on the serving mode derived from the media column's
type:

- **URL mode** (string column) — the stored URL, verbatim. The client uses it
  directly; the server never fetches it.
- **Binary mode** (binary column) — the opaque reference
  `bifrost-media://<table>/<id>`, resolved by `GET {Path}/media/{table}/{id}` on
  the chat endpoints. The route is **auth-gated and re-authorizing**: every fetch
  reads the row through the intent executor under the caller's own context, so
  tenant isolation and policy scope apply on each request and the reference
  carries no secret — it needs no signature, no expiry, and no token store.
  Unknown tables, non-media tables, URL-mode tables, cross-tenant rows, and
  nonexistent rows all answer the same 404. The response content type is sniffed
  from the bytes' magic numbers (png/jpeg/gif/webp; `application/octet-stream`
  otherwise) — the media contract has no content-type column. The bytes
  themselves never ride the lookup payload or the model conversation.

Alongside the SSE `tool` events, a media-bearing tool result is followed by one
`media` event the client renders from:

```text
event: media
data: {"toolName":"media_documents","items":[
  {"id":7,"mediaReference":"bifrost-media://documents/7","caption":"Contract page 1"}]}
```

**Vision** — with `chat-media-vision: enabled` (valid only on binary-mode media
columns; the validator rejects it on URL columns rather than leaving it silently
dead) the tool gains one extra input, `view_image_id`. Set to a single row id —
it cannot be combined with the lookup arguments — the connector loads that row's
bytes through the same caller-scoped intent read and attaches them to the tool
result as a base64 image block, so the model can actually look at the image.
Guard rails, all model-visible errors rather than silent drops: the row must be
visible to the caller (cross-tenant and nonexistent ids read identically), the
bytes must sniff as png/jpeg/gif/webp, and the raw size is capped by
`ChatConnectorOptions.MediaVisionByteCap` (default ~3.5 MB, keeping the base64
under the provider's per-image limit). With vision **off**, the `view_image_id`
input does not exist in the tool schema at all and media bytes never leave the
server.

### Plan tools: human-gated writes

A `chat-connector: plan` table becomes one tool **per operation in its
`chat-plan-operations` allow-list** — `plan_insert_<table>`,
`plan_update_<table>`, `plan_delete_<table>`. A disallowed operation's tool is
**absent from the schema entirely** (not present-and-refused), and `delete` is
never implied.

The security contract, in order:

1. **A plan tool call never writes.** Execution validates the model's rows
   (unknown and hidden columns rejected identically, database-generated columns
   not writable, update/delete rows must carry the full primary key, row count
   capped at `ChatConnectorOptions.PlanRowCap`, default 20) and produces a
   **write proposal**. The tool loop relays it to the client (SSE
   `confirmation` event) and **parks** — between provider turns, so no
   upstream request is held open — until the user decides.
2. **A confirmed proposal executes through the batch mutation-intent seam under
   the original caller's identity.** The full mutation transformer chain
   (tenant stamp, policy, validation, audit, encryption) and the in-transaction
   hooks (change-history trail, CDC outbox) apply by construction, and **all
   rows commit in one transaction** — a transformer veto on any row rolls the
   whole proposal back and the model receives a sanitized `is_error` result:
   there is no partial batch.
3. **A denied proposal writes nothing.** The model receives a declined
   (non-error) tool result carrying the user's reason and continues — it can
   revise the proposal. A proposal nobody answers **denies itself** after
   `ChatConnectorOptions.PlanConfirmationTimeout` (default 5 minutes).
4. **The confirmation id is single-use** — cryptographically random, bound to
   the requesting identity (tenant + user) and conversation, and it dies with
   the stream that parked on it. An unknown, reused, cross-identity, or
   cross-conversation id resolves nothing, indistinguishably.
5. **Both outcomes are recorded in the conversation** as a system-role
   transcript row (`[plan proposal <id> (<operation> on <table>):
   approved/denied…]`), so the stored history is faithful to what the user
   authorized.

The confirmation endpoint and event shapes are in the
[LLM Chat Endpoints guide](/guides/llm-chat/#plan-confirmations). The
[Chat Connectors guide](/guides/chat-connectors/) is the operator's reference —
metadata per connector type, tool generation, caps, the confirmation protocol,
vision caveats, and custom connector registration. See the
[configuration reference](/reference/configuration/#chat-connector-metadata) for
the key table.

See the [configuration reference](/reference/configuration/#chat-metadata) for the
chat pair's key table.
