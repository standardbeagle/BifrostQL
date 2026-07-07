import { ConnectionInfo } from './types';
import { parseConnectionInfo, sanitizeConnectionInfo } from './sanitize-connection';

const SESSION_KEY = 'bifrostql_active_session';

export function saveSession(info: ConnectionInfo | null) {
  if (info) sessionStorage.setItem(SESSION_KEY, JSON.stringify(sanitizeConnectionInfo(info)));
  else sessionStorage.removeItem(SESSION_KEY);
}

export function loadSession(): ConnectionInfo | null {
  try {
    const stored = sessionStorage.getItem(SESSION_KEY);
    if (!stored) return null;

    const parsed = parseConnectionInfo(JSON.parse(stored));
    if (!parsed) {
      sessionStorage.removeItem(SESSION_KEY);
      return null;
    }

    const sanitized = sanitizeConnectionInfo(parsed);
    const sanitizedJson = JSON.stringify(sanitized);
    if (sanitizedJson !== stored) {
      sessionStorage.setItem(SESSION_KEY, sanitizedJson);
    }
    return sanitized;
  } catch { return null; }
}
