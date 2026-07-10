import { ConnectionInfo, Provider } from './types';

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

/**
 * Non-secret structured fields extracted from an ADO.NET-style connection
 * string. `ssl` is left `undefined` when the string carries no SSL/TLS key so
 * callers can distinguish "unset" from an explicit disable.
 */
export interface AdoConnectionFields {
  host?: string;
  port?: number;
  database?: string;
  username?: string;
  ssl?: boolean;
}

/**
 * Parse the non-sensitive structured fields out of an ADO.NET-style connection
 * string. Password/Pwd and other secret keys are deliberately ignored — the
 * secret is collected elsewhere (the isolated credential prompt) and never
 * flows through this parse.
 *
 * Each provider uses slightly different key names (Server vs Host, User Id vs
 * Username vs Uid, etc); they are normalised into a single shape here rather
 * than making every caller branch. Unknown keys are silently ignored.
 */
export function parseAdoConnectionString(
  connectionString: string,
  provider: Provider,
): AdoConnectionFields {
  const parts = connectionString
    .split(';')
    .map((p) => p.trim())
    .filter((p) => p.length > 0)
    .map((p) => {
      const eq = p.indexOf('=');
      if (eq < 0) return ['', ''] as const;
      return [p.slice(0, eq).trim().toLowerCase(), p.slice(eq + 1).trim()] as const;
    });
  const lookup = new Map<string, string>(parts);
  const get = (...keys: string[]): string | undefined => {
    for (const k of keys) {
      const v = lookup.get(k);
      if (v !== undefined && v.length > 0) return v;
    }
    return undefined;
  };

  let host = get('server', 'host', 'data source');
  let port: number | undefined;
  if (provider === 'sqlserver' && host && host.includes(',')) {
    // SQL Server encodes "host,port" in the Server field.
    const comma = host.indexOf(',');
    port = parsePort(host.slice(comma + 1));
    host = host.slice(0, comma);
  }
  if (port === undefined) {
    port = parsePort(get('port'));
  }

  const database = get('database', 'initial catalog');
  const username = get('user id', 'username', 'uid', 'user');
  const sslModeRaw = get('sslmode', 'ssl mode');
  const ssl = sslModeRaw ? /require|verify|true|prefer/i.test(sslModeRaw) : undefined;

  return { host, port, database, username, ssl };
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
