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
This slice establishes the **metadata contract and fail-fast validation**. The chat
read/write surface (SSE streaming) is a later slice.
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
- The conversations table needs a **single-column primary key** (composite keys are
  rejected with a clear error), and `chat-conversation-fk` must actually reference it
  through a declared foreign key or an explicit `join` metadata rule.
- Both chat tables must be **published** (not `visibility: hidden`) and must not be
  change-history **targets**. They *may* be history-**enabled** — recording the chat's
  own change trail composes.

See the [configuration reference](/reference/configuration/#chat-metadata) for the
key table.
