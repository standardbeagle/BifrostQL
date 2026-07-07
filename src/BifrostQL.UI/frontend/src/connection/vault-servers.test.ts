/**
 * Unit tests for the vault-server fetch wrappers. Mocks globalThis.fetch so no
 * network or backend is required (Vitest node env).
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fetchVaultServers, connectVaultServer } from './vault-servers';

const g = globalThis as any;

beforeEach(() => { g.fetch = vi.fn(); });
afterEach(() => { vi.restoreAllMocks(); delete g.fetch; });

describe('fetchVaultServers', () => {
  it('returns the parsed server list with no error on a successful response', async () => {
    const servers = [{ name: 'a', provider: 'postgres' }];
    g.fetch.mockResolvedValue({ ok: true, json: async () => servers });

    await expect(fetchVaultServers()).resolves.toEqual({ servers, error: null });
    expect(g.fetch).toHaveBeenCalledWith('/api/vault/servers');
  });

  it('surfaces an error (not a silent empty list) on a non-ok response', async () => {
    g.fetch.mockResolvedValue({ ok: false, status: 503, json: async () => ({}) });

    const result = await fetchVaultServers();
    expect(result.servers).toEqual([]);
    expect(result.error).toContain('503');
  });

  it('surfaces the failure detail when fetch rejects', async () => {
    g.fetch.mockRejectedValue(new Error('network down'));

    const result = await fetchVaultServers();
    expect(result.servers).toEqual([]);
    expect(result.error).toContain('network down');
  });
});

describe('connectVaultServer', () => {
  it('POSTs the name and returns the parsed result', async () => {
    const result = { success: true, provider: 'postgres', database: 'db' };
    g.fetch.mockResolvedValue({ ok: true, json: async () => result });

    await expect(connectVaultServer('prod')).resolves.toEqual(result);

    const [url, init] = g.fetch.mock.calls[0];
    expect(url).toBe('/api/vault/connect');
    expect(init.method).toBe('POST');
    expect(init.headers['Content-Type']).toBe('application/json');
    expect(JSON.parse(init.body)).toEqual({ name: 'prod' });
  });

  it('propagates an error result body', async () => {
    g.fetch.mockResolvedValue({ ok: false, json: async () => ({ success: false, error: 'nope' }) });
    await expect(connectVaultServer('x')).resolves.toEqual({ success: false, error: 'nope' });
  });
});
