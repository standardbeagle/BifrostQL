import { describe, expect, it } from 'vitest';
import { mapFrame } from './chat-api';
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

  it('ignores unknown event names so the server can add events later', () => {
    expect(mapFrame('future-event', '{"x":1}')).toBeNull();
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
