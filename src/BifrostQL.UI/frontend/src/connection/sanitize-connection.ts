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
