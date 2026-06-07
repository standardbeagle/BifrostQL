import { ConnectionInfo } from './types';
import { sanitizeConnectionInfo } from './sanitize-connection';

const RECENT_CONNECTIONS_KEY = 'bifrostql_recent_connections';
export const MAX_RECENT_CONNECTIONS = 5;

export const saveRecentConnections = (connections: ConnectionInfo[]): void => {
  if (typeof window === 'undefined') return;
  try {
    const sanitized = connections.map(sanitizeConnectionInfo);
    localStorage.setItem(RECENT_CONNECTIONS_KEY, JSON.stringify(sanitized));
  } catch (error) {
    console.warn('Failed to save recent connections:', error);
  }
};

export const loadRecentConnections = (): ConnectionInfo[] => {
  if (typeof window === 'undefined') return [];
  try {
    const stored = localStorage.getItem(RECENT_CONNECTIONS_KEY);
    if (stored) {
      const connections = JSON.parse(stored) as ConnectionInfo[];
      const sanitized = connections.map(sanitizeConnectionInfo).slice(0, MAX_RECENT_CONNECTIONS);
      const sanitizedJson = JSON.stringify(sanitized);
      if (sanitizedJson !== stored) {
        localStorage.setItem(RECENT_CONNECTIONS_KEY, sanitizedJson);
      }
      return sanitized;
    }
  } catch (error) {
    console.warn('Failed to load recent connections:', error);
  }
  return [];
};
