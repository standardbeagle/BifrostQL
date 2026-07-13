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
This page covers the metadata contract only — the generated explore, media, and
plan tools land in later slices. See the
[configuration reference](/reference/configuration/#chat-connector-metadata) for
the key table.

See the [configuration reference](/reference/configuration/#chat-metadata) for the
chat pair's key table.
