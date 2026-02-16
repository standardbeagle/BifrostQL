// @vitest-environment node
import { describe, it, expect, afterEach } from 'vitest';
import { QueryClient } from '@tanstack/react-query';
import { getQueryClient, resetServerQueryClient } from './get-query-client';

describe('getQueryClient', () => {
  afterEach(() => {
    resetServerQueryClient();
  });

  it('returns a QueryClient instance', () => {
    const client = getQueryClient();
    expect(client).toBeInstanceOf(QueryClient);
  });

  it('returns the same instance on repeated server-side calls', () => {
    const first = getQueryClient();
    const second = getQueryClient();
    expect(first).toBe(second);
  });

  it('returns a fresh instance after resetServerQueryClient', () => {
    const first = getQueryClient();
    resetServerQueryClient();
    const second = getQueryClient();
    expect(first).not.toBe(second);
  });

  it('sets default staleTime of 60 seconds', () => {
    const client = getQueryClient();
    const defaults = client.getDefaultOptions();
    expect(defaults.queries?.staleTime).toBe(60_000);
  });
});
