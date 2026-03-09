/**
 * BifrostQL Connection UI Components
 *
 * A set of React components for database connection management in the BifrostQL
 * desktop application. These components provide a modern, accessible interface
 * for connecting to databases across multiple providers.
 *
 * @module connection
 */

export { ConnectionForm } from './ConnectionForm';
export { ProviderSelect } from './ProviderSelect';
export { QuickStart } from './QuickStart';
export { WelcomePanel } from './WelcomePanel';
export { TestDatabaseDialog } from './TestDatabaseDialog';
export * from './types';

/**
 * Utility functions for managing recent connections in localStorage
 */
export {
  saveRecentConnections,
  loadRecentConnections,
  MAX_RECENT_CONNECTIONS,
} from './WelcomePanel';
