import { ConnectionInfo } from './types';

const REDACTED_VALUE = '<redacted>';

const SENSITIVE_CONNECTION_STRING_KEYS = new Set([
  'access token',
  'accesstoken',
  'auth token',
  'authtoken',
  'password',
  'pass',
  'passwd',
  'pwd',
  'secret',
  'token',
]);

function normalizeConnectionStringKey(key: string): string {
  return key.trim().toLowerCase().replace(/[_-]+/g, ' ');
}

export function redactConnectionStringSecrets(connectionString: string): string {
  if (!connectionString) return '';

  return connectionString
    .split(';')
    .map((part) => {
      const equalsIndex = part.indexOf('=');
      if (equalsIndex < 0) return part;

      const key = part.slice(0, equalsIndex);
      if (!SENSITIVE_CONNECTION_STRING_KEYS.has(normalizeConnectionStringKey(key))) {
        return part;
      }

      return `${key}=${REDACTED_VALUE}`;
    })
    .join(';');
}

export function sanitizeConnectionInfo(connection: ConnectionInfo): ConnectionInfo {
  return {
    ...connection,
    connectionString: redactConnectionStringSecrets(connection.connectionString ?? ''),
  };
}

export function parsePort(value: unknown): number | undefined {
  if (typeof value !== 'string' && typeof value !== 'number') return undefined;
  const text = String(value).trim();
  if (!/^\d+$/.test(text)) return undefined;
  const port = Number(text);
  if (!Number.isSafeInteger(port) || port < 1 || port > 65535) return undefined;
  return port;
}

export function parseConnectionInfo(value: unknown): ConnectionInfo | null {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    return null;
  }

  const connection = value as Record<string, unknown>;
  if (
    typeof connection.id !== 'string' ||
    typeof connection.name !== 'string' ||
    typeof connection.connectionString !== 'string' ||
    typeof connection.provider !== 'string'
  ) {
    return null;
  }

  return connection as unknown as ConnectionInfo;
}
