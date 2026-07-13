# BifrostQL Chat Example

A small Vite + React SPA over the BifrostQL chat module:

- **Sidebar** — conversation list loaded with a plain GraphQL query on the
  published `conversations` table, plus a create/switch flow.
- **Chat pane** — message history via GraphQL (chronological), a send box that
  POSTs to the chat SSE endpoint, incremental rendering of streamed `delta`
  events, typed error states (refusal and `tool-loop-limit` rendered
  distinctly), a `409 stream-in-progress` notice, and a Stop button that
  aborts the fetch.
- **Connector UX** — live `tool` events render as inline status chips
  (calling… / result summary), `media` events render as an inline image grid
  (binary `bifrost-media://` references fetched through the auth-gated media
  route with credentials, stored URLs rendered directly, captions as alt
  text), and `confirmation` events render a proposal card (operation, table,
  rows, approve/deny with an optional ≤500-char reason) while the stream sits
  parked waiting for your decision. Tool chips and media are per-turn display
  state — the server persists final answer text only.

The GraphQL client and SSE parser are local to this example on purpose — the
repo intentionally keeps its GraphQL clients separate (see the root
`AGENTS.md`, "Two Client Stacks").

Endpoint contract: [LLM Chat Endpoints guide](../../docs/src/content/docs/guides/llm-chat.md).
Metadata contract: [Chat over Your Tables](../../docs/src/content/docs/concepts/chat.md).
Connector guide: [Chat Connectors](../../docs/src/content/docs/guides/chat-connectors.md).

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

The chat pair is `conversations(id, title)` and
`messages(id, conversation_id, role, content, created_at)` — integer identity
keys (the chat module orders by them). The demo config skips tenancy; to demo
isolation, add `tenant_id` columns and `tenant-filter: tenant_id` to both
tables' metadata.

Around the pair, the sample grows three **connector scenarios** (see the
[Chat Connectors guide](../../docs/src/content/docs/guides/chat-connectors.md)):

| Table | Connector | Demo |
|---|---|---|
| `orders` | `explore` | The model queries orders in chat (read-only, capped) |
| `products` | `media` | Product images render inline — `image` is a BLOB, so references resolve through the auth-gated media route; `caption` is the alt text |
| `blog_posts` + `publish_schedule` | `explore` + `plan` (insert,update) | The model proposes schedule writes; nothing lands until you approve the proposal card |

### 2. Point the host at it

Replace the contents of `src/BifrostQL.Host/appsettings.json` with
[`sample/appsettings.chat.json`](sample/appsettings.chat.json) (keep a copy of
the original to restore afterwards). The interesting parts are the chat
metadata keys:

```json
"Metadata": [
  ":root { auto-join: true; de-pluralize: false; default-limit: 100; }",
  "main.conversations { chat-conversations: enabled; chat-title: title }",
  "main.messages { chat-messages: enabled; chat-role: role; chat-content: content; chat-conversation-fk: conversation_id; chat-created-at: created_at }",
  "main.orders { chat-connector: explore; chat-tool-description: Customer orders with status and totals }",
  "main.products { chat-connector: media; chat-media-column: image; chat-media-caption: caption; chat-tool-description: Product catalog with photos }",
  "main.blog_posts { chat-connector: explore; chat-tool-description: Blog posts the publish schedule refers to by post_id }",
  "main.publish_schedule { chat-connector: plan; chat-plan-operations: insert,update; chat-tool-description: Publish schedule for blog posts — every write needs user approval }"
]
```

To let the model **look at** the product images itself (not just hand them to
the UI), add the vision flag to the products line — left off by default
because every viewed image rides the provider request as base64 (cost) and
image contents become model input (prompt-injection surface, see the guide's
caveats):

```text
main.products { chat-connector: media; chat-media-column: image;
                chat-media-caption: caption; chat-media-vision: enabled }
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
splits, CRLF/LF/lone-CR line endings, a CRLF straddling a chunk boundary,
comments, discarded trailing frames, multi-byte splits), the
event-name-to-typed-event mapping (including tool / media / confirmation /
confirmation-resolved and the non-JSON-data guard), and the GraphQL query
builders plus the media reference-to-route mapping. The streaming UI itself is
verified with the manual script below — no browser E2E.

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

### Connector smoke script

9. **Explore** — ask "Which orders are still pending, largest total first?"
   An `explore_orders` chip appears (calling…, then a result summary) and the
   answer names Cara Okafor, Faisal Khan, and Bo Lindqvist from the seed data.
10. **Media** — ask "Show me the product images." A `media_products` chip runs
    and an inline image grid renders the three seeded swatches, captions as
    alt text. Each image is fetched through
    `GET /_chat/media/products/<id>` — the auth-gated binary route — not from
    the tool payload.
11. **Plan, approve** — ask "Schedule the 'Launching the new catalog' post for
    August 1st at 10:00." The model explores `blog_posts` for the post id,
    then a proposal card appears (insert on `publish_schedule`, the row
    visible), the stream parks ("waiting for your approval"), and the send box
    stays busy. Click **Approve**: the stream resumes, the answer confirms,
    and the row landed —

    ```bash
    sqlite3 chat-demo.db "SELECT post_id, publish_at, status FROM publish_schedule"
    ```

12. **Plan, deny** — ask for another schedule, then click **Deny** with a
    reason like "wrong date — use September 1st". Nothing is written (re-run
    the `sqlite3` check), the model receives your reason, and it revises —
    typically proposing a corrected row in a fresh card on the same stream.
13. **Timeout / second tab** — leave a proposal card unanswered for the
    confirmation timeout (default 5 minutes) or resolve it from another tab:
    the card clears itself when the stream's `confirmation-resolved` event
    arrives, and an unanswered proposal denies itself.

### About failed sends

If a send fails with a 5xx or a network error, the UI deliberately does
**not** auto-retry: the user message may already have been persisted
(`message-accepted` semantics), so an automatic retry could store the question
twice and trigger a second completion. The error notice tells you the message
may already be saved and offers a history reload instead.
