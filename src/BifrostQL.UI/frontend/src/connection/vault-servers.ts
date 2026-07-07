import type { VaultServer } from './types';

/**
 * Result of loading saved vault servers. `error` is non-null when the list
 * could not be loaded, so the UI can surface the failure instead of silently
 * presenting an empty list that looks like "no saved servers".
 */
export interface VaultServersResult {
  servers: VaultServer[];
  error: string | null;
}

export async function fetchVaultServers(): Promise<VaultServersResult> {
  try {
    const resp = await fetch('/api/vault/servers');
    if (!resp.ok) {
      return { servers: [], error: `Failed to load saved servers (HTTP ${resp.status}).` };
    }
    return { servers: await resp.json(), error: null };
  } catch (e) {
    const detail = e instanceof Error ? e.message : String(e);
    return { servers: [], error: `Failed to load saved servers: ${detail}` };
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
