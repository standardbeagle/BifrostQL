/**
 * Plain-fetch GraphQL client, local to this example on purpose.
 *
 * The repo intentionally keeps several small GraphQL clients side by side
 * (see AGENTS.md "Two Client Stacks") — this example does not import
 * graphql-client.ts, fetcher.ts, or QueryTransport. It only needs two queries
 * against the demo chat tables, so a ~40-line client is the whole data layer.
 *
 * Table and column names below are constants matching the sample metadata in
 * sample/appsettings.chat.json — never user input — so building the query
 * text from them is safe. The only dynamic value, the conversation id, is
 * validated as an integer before it is inlined.
 */

const GRAPHQL_URL = '/graphql';

export interface ConversationRow {
  id: number;
  title: string | null;
}

export interface MessageRow {
  id: number;
  role: string;
  content: string | null;
  created_at: string | null;
}

export async function graphqlRequest<T>(query: string): Promise<T> {
  const response = await fetch(GRAPHQL_URL, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    credentials: 'include',
    body: JSON.stringify({ query }),
  });
  if (!response.ok) {
    throw new Error(`GraphQL request failed: HTTP ${response.status}`);
  }
  const body = (await response.json()) as {
    data?: T;
    errors?: { message: string }[];
  };
  if (body.errors?.length) {
    throw new Error(`GraphQL errors: ${body.errors.map((e) => e.message).join('; ')}`);
  }
  if (body.data === undefined || body.data === null) {
    throw new Error('GraphQL response carried no data.');
  }
  return body.data;
}

/** Conversation list, newest first (integer identity key = creation order). */
export function conversationsQuery(): string {
  return `{ conversations(sort: [id_desc]) { data { id title } } }`;
}

/** Message history for one conversation, chronological. */
export function messagesQuery(conversationId: number): string {
  if (!Number.isInteger(conversationId)) {
    throw new Error(`conversationId must be an integer, got: ${conversationId}`);
  }
  return (
    `{ messages(filter: { conversation_id: { _eq: ${conversationId} } }, ` +
    `sort: [created_at_asc, id_asc]) { data { id role content created_at } } }`
  );
}

export async function fetchConversations(): Promise<ConversationRow[]> {
  const data = await graphqlRequest<{ conversations: { data: ConversationRow[] } }>(
    conversationsQuery(),
  );
  return data.conversations.data;
}

export async function fetchMessages(conversationId: number): Promise<MessageRow[]> {
  const data = await graphqlRequest<{ messages: { data: MessageRow[] } }>(
    messagesQuery(conversationId),
  );
  return data.messages.data;
}
