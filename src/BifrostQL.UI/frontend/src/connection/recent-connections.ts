import { ConnectionInfo } from './types';

const RECENT_CONNECTIONS_KEY = 'bifrostql_recent_connections';
export const MAX_RECENT_CONNECTIONS = 5;

export const saveRecentConnections = (connections: ConnectionInfo[]): void => {
  if (typeof window === 'undefined') return;
  try {
    localStorage.setItem(RECENT_CONNECTIONS_KEY, JSON.stringify(connections));
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
      return connections.slice(0, MAX_RECENT_CONNECTIONS);
    }
  } catch (error) {
    console.warn('Failed to load recent connections:', error);
  }
  return [];
};
