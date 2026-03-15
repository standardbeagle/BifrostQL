import { ConnectionInfo } from './types';

const SESSION_KEY = 'bifrostql_active_session';

export function saveSession(info: ConnectionInfo | null) {
  if (info) sessionStorage.setItem(SESSION_KEY, JSON.stringify(info));
  else sessionStorage.removeItem(SESSION_KEY);
}

export function loadSession(): ConnectionInfo | null {
  try {
    const stored = sessionStorage.getItem(SESSION_KEY);
    return stored ? JSON.parse(stored) : null;
  } catch { return null; }
}
