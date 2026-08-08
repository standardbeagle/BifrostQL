import { ConnectionInfo } from './types';
import { parseConnectionInfo, sanitizeConnectionInfo } from './sanitize-connection';

// Storage-key prefix convention: two prefixes coexist in web storage.
// Older keys use `bifrostql_` (this session key, recent-connections,
// sql-history, saved-forms, forms-migration flag); newer keys use
// `bifrost-ui:` (transport, profile). Existing keys are FROZEN — renaming
// one would orphan users' stored data — but NEW keys should use the
// `bifrost-ui:` prefix.
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
