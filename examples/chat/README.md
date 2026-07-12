# BifrostQL Chat Example

A small Vite + React SPA over the BifrostQL chat module:

- **Sidebar** — conversation list loaded with a plain GraphQL query on the
  published `conversations` table, plus a create/switch flow.
- **Chat pane** — message history via GraphQL (chronological), a send box that
  POSTs to the chat SSE endpoint, incremental rendering of streamed `delta`
  events, typed error states (refusal rendered distinctly), a `409
  stream-in-progress` notice, and a Stop button that aborts the fetch.

The GraphQL client and SSE parser are local to this example on purpose — the
repo intentionally keeps its GraphQL clients separate (see the root
`AGENTS.md`, "Two Client Stacks").

Endpoint contract: [LLM Chat Endpoints guide](../../docs/src/content/docs/guides/llm-chat.md).
Metadata contract: [Chat over Your Tables](../../docs/src/content/docs/concepts/chat.md).

## Backend setup

The demo runs against `src/BifrostQL.Host` with a SQLite database. Two things
do not work out of the box and need a temporary (uncommitted) host tweak:

1. The stock host does not map the chat endpoints (`UseBifrostChat` is opt-in).
2. The chat endpoints are fail-closed: an anonymous caller gets `401` before
   anything else runs, so the demo stamps a dev-only identity. In a real
   deployment use local auth, OIDC, or JWT bearer instead (see the
   [authentication guide](../../docs/src/content/docs/guides/authentication.md)).

### 1. Create the database (repo root)

```bash
sqlite3 chat-demo.db < examples/chat/sample/chat-demo.sql
```

The schema is two tables — `conversations(id, title)` and
`messages(id, conversation_id, role, content, created_at)` — with integer
identity keys (the chat module orders by them). The demo config skips tenancy;
to demo isolation, add `tenant_id` columns and `tenant-filter: tenant_id` to
both tables' metadata.

### 2. Point the host at it

Replace the contents of `src/BifrostQL.Host/appsettings.json` with
[`sample/appsettings.chat.json`](sample/appsettings.chat.json) (keep a copy of
the original to restore afterwards). The interesting parts are the chat
metadata keys:

```json
"Metadata": [
  ":root { auto-join: true; de-pluralize: false; default-limit: 100; }",
  "main.conversations { chat-conversations: enabled; chat-title: title }",
  "main.messages { chat-messages: enabled; chat-role: role; chat-content: content; chat-conversation-fk: conversation_id; chat-created-at: created_at }"
]
```

### 3. Enable the chat endpoints in the host

In `src/BifrostQL.Host/Program.cs`, immediately before `app.UseBifrostQL();`,
add (temporarily — do not commit):

```csharp
// DEV DEMO ONLY: the chat endpoints reject anonymous callers with 401, so
// stamp a fake authenticated identity for local requests. Replace with real
// authentication for anything beyond this demo.
app.Use(async (context, next) =>
{
    context.User = new System.Security.Claims.ClaimsPrincipal(
        new System.Security.Claims.ClaimsIdentity(
            new[] { new System.Security.Claims.Claim("sub", "demo-user") },
            authenticationType: "DevDemo"));
    await next();
});

app.UseBifrostChat(chat =>
{
    chat.SystemPrompt = "You are a helpful assistant for a demo chat application.";
});
```

### 4. Run the host

`UseBifrostChat` resolves the completion service at startup, so the host
refuses to start without an API key:

```bash
export ANTHROPIC_API_KEY=sk-ant-...   # required
dotnet run --project src/BifrostQL.Host
```

The host listens on `http://localhost:5077` (see its `launchSettings.json`).
Model and token ceiling come from the `BifrostQL:Chat` section in the sample
appsettings (`Model`, `MaxTokens`); the api key can also be set as
`BifrostQL:Chat:ApiKey`.

## Frontend

```bash
pnpm install                      # once, at the repo root (single lockfile)
pnpm --dir examples/chat dev      # http://localhost:5173
```

The Vite dev server proxies `/graphql` and `/_chat` to the host. If your host
is elsewhere:

```bash
BIFROST_URL=http://localhost:5000 pnpm --dir examples/chat dev
```

## Build and tests

```bash
pnpm --dir examples/chat build    # tsc + vite build
pnpm --dir examples/chat test     # vitest: SSE parser + event mapping + query builders
```

The scripted tests cover the SSE wire parsing (multi-line data, chunk-boundary
splits, CRLF, comments, discarded trailing frames, multi-byte splits), the
event-name-to-typed-event mapping, and the GraphQL query builders. The
streaming UI itself is verified with the manual script below — no browser E2E.

## Manual smoke script

1. Start the host and the dev server as above, open `http://localhost:5173`.
2. Click **+ New conversation** — it appears at the top of the sidebar and is
   selected.
3. Type a question and **Send** — the user bubble appears immediately, then an
   assistant bubble streams in incrementally with a blinking cursor.
4. When the stream finishes, the assistant bubble finalizes and the history is
   reloaded from GraphQL — what you see is what the database holds.
5. Click **+ New conversation** again, send another message, then switch back
   and forth in the sidebar — each conversation reloads its own history.
6. While a response is streaming, press **Stop** — the fetch aborts, a
   "cancelled" notice appears, and the reloaded history shows your question
   was kept while the partial answer was discarded.
7. To see the `409` notice, POST a second message from another tab/curl while
   one is streaming: the UI reports "already streaming".
8. Error states: stop the host mid-stream to see the stream-error notice; a
   refusal ends the stream with a distinct refusal notice and discards the
   partial deltas (nothing is persisted for the assistant).

### About failed sends

If a send fails with a 5xx or a network error, the UI deliberately does
**not** auto-retry: the user message may already have been persisted
(`message-accepted` semantics), so an automatic retry could store the question
twice and trigger a second completion. The error notice tells you the message
may already be saved and offers a history reload instead.
