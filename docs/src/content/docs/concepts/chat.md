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
  qualifies; no extra columns needed.
- **`media`** — the model can serve an image/file column (`chat-media-column`).
  The serving mode is derived from the column type — a binary column serves bytes,
  a string column serves URLs — and `chat-media-vision: enabled` opts the content
  into vision input.
- **`plan`** — the model can propose gated writes, limited to the
  `chat-plan-operations` allow-list. `delete` is never implied; it must be listed
  explicitly, and the table must have a primary key.

Connector configuration fails fast at model load with the same rigor as the chat
pair: unknown tokens, stray media/plan keys without their type token, missing or
wrongly-typed columns, and unpublished or history-target tables are all rejected.
The chat pair tables themselves **may** be connectors — "explore my own
conversation history" is a legitimate tool, and the row-scope transformers guard
connector reads exactly as they guard the chat store's.

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

The generated explore, media, and plan tools land in later slices. See the
[configuration reference](/reference/configuration/#chat-connector-metadata) for
the key table.

See the [configuration reference](/reference/configuration/#chat-metadata) for the
chat pair's key table.
