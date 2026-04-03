import type { VaultServer } from './types';

export async function fetchVaultServers(): Promise<VaultServer[]> {
  try {
    const resp = await fetch('/api/vault/servers');
    if (!resp.ok) return [];
    return await resp.json();
  } catch {
    return [];
  }
}

export async function connectVaultServer(
  name: string,
): Promise<{ success: boolean; provider?: string; server?: string; database?: string; name?: string; error?: string }> {
  const resp = await fetch('/api/vault/connect', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name }),
  });
  return resp.json();
}
