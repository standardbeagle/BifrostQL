import { describe, expect, it } from 'vitest';
import { mapFrame, mediaFetchPath } from './chat-api';
import { conversationsQuery, messagesQuery } from './graphql';

describe('mapFrame', () => {
  it('maps the documented chat events to typed events', () => {
    expect(mapFrame('message-accepted', '{"userMessageId":7,"conversationId":42}')).toEqual({
      type: 'message-accepted',
      userMessageId: 7,
      conversationId: 42,
    });
    expect(mapFrame('delta', '{"text":"chunk"}')).toEqual({ type: 'delta', text: 'chunk' });
    expect(mapFrame('done', '{"assistantMessageId":8,"stopReason":"truncated"}')).toEqual({
      type: 'done',
      assistantMessageId: 8,
      stopReason: 'truncated',
    });
    expect(
      mapFrame('error', '{"code":"refusal","message":"declined","refusalCategory":"policy"}'),
    ).toEqual({
      type: 'error',
      code: 'refusal',
      message: 'declined',
      refusalCategory: 'policy',
      retryable: undefined,
    });
  });

  it('maps the connector events: tool, media, confirmation, confirmation-resolved', () => {
    expect(mapFrame('tool', '{"name":"explore_orders","phase":"call","summary":"querying"}')).toEqual({
      type: 'tool',
      name: 'explore_orders',
      phase: 'call',
      summary: 'querying',
    });
    expect(
      mapFrame(
        'media',
        '{"toolName":"media_products","items":[{"id":7,"mediaReference":"bifrost-media://products/7","caption":"Red mug"}]}',
      ),
    ).toEqual({
      type: 'media',
      toolName: 'media_products',
      items: [{ id: 7, mediaReference: 'bifrost-media://products/7', caption: 'Red mug' }],
    });
    expect(
      mapFrame(
        'confirmation',
        '{"confirmationId":"abc","toolName":"plan_insert_publish_schedule","table":"publish_schedule",' +
          '"operation":"insert","rows":[{"post_id":1}],"summary":"insert 1 row"}',
      ),
    ).toEqual({
      type: 'confirmation',
      confirmationId: 'abc',
      toolName: 'plan_insert_publish_schedule',
      table: 'publish_schedule',
      operation: 'insert',
      rows: [{ post_id: 1 }],
      summary: 'insert 1 row',
    });
    expect(
      mapFrame('confirmation-resolved', '{"confirmationId":"abc","approved":false,"reason":"wrong date"}'),
    ).toEqual({
      type: 'confirmation-resolved',
      confirmationId: 'abc',
      approved: false,
      reason: 'wrong date',
    });
  });

  it('maps the tool-loop-limit error code like any other error', () => {
    expect(mapFrame('error', '{"code":"tool-loop-limit","message":"loop exceeded"}')).toEqual({
      type: 'error',
      code: 'tool-loop-limit',
      message: 'loop exceeded',
      refusalCategory: undefined,
      retryable: undefined,
    });
  });

  it('ignores unknown event names so the server can add events later', () => {
    expect(mapFrame('future-event', '{"x":1}')).toBeNull();
  });

  it('skips frames whose data is not JSON instead of tearing the stream down', () => {
    expect(mapFrame('delta', 'not-json')).toBeNull();
    expect(mapFrame('message', '')).toBeNull();
  });
});

describe('mediaFetchPath', () => {
  it('maps binary bifrost-media references onto the auth-gated media route', () => {
    expect(mediaFetchPath('bifrost-media://products/7')).toBe('/_chat/media/products/7');
  });

  it('URL-encodes the table and id segments', () => {
    expect(mediaFetchPath('bifrost-media://a b/c?d')).toBe('/_chat/media/a%20b/c%3Fd');
  });

  it('returns null for stored URLs, which render directly', () => {
    expect(mediaFetchPath('https://cdn.example.com/mug.png')).toBeNull();
    expect(mediaFetchPath('/relative/image.png')).toBeNull();
  });
});

describe('query builders', () => {
  it('lists conversations newest first', () => {
    expect(conversationsQuery()).toBe(
      '{ conversations(sort: [id_desc]) { data { id title } } }',
    );
  });

  it('fetches messages chronologically with created_at ties broken by id', () => {
    expect(messagesQuery(42)).toBe(
      '{ messages(filter: { conversation_id: { _eq: 42 } }, ' +
        'sort: [created_at_asc, id_asc]) { data { id role content created_at } } }',
    );
  });

  it('rejects non-integer conversation ids', () => {
    expect(() => messagesQuery(1.5)).toThrow(/integer/);
    expect(() => messagesQuery(Number.NaN)).toThrow(/integer/);
  });
});
