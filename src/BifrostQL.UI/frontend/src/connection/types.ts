/**
 * Supported database providers
 */
export type Provider = 'sqlserver' | 'postgres' | 'mysql' | 'sqlite';

/**
 * Display information for a database provider
 */
export interface ProviderInfo {
  id: Provider;
  name: string;
  icon: string;
  description: string;
}

/**
 * Provider display info for UI rendering
 */
export const PROVIDERS: ProviderInfo[] = [
  { id: 'sqlserver', name: 'SQL Server', icon: 'S', description: 'Microsoft SQL Server' },
  { id: 'postgres', name: 'PostgreSQL', icon: 'P', description: 'PostgreSQL database' },
  { id: 'mysql', name: 'MySQL', icon: 'M', description: 'MySQL / MariaDB' },
  { id: 'sqlite', name: 'SQLite', icon: 'L', description: 'SQLite file database' },
];

/**
 * QuickStart schema templates
 */
export type QuickStartSchema = 'blog' | 'ecommerce' | 'crm' | 'classroom' | 'project-tracker';

/**
 * Data size options for quickstart
 */
export type DataSize = 'sample' | 'full';

/**
 * Authentication methods for SQL Server connection
 */
export enum AuthMethod {
  SqlServer = 'sql-server',
  Windows = 'windows'
}

/**
 * Connection form state
 */
export interface ConnectionFormData {
  server: string;
  database: string;
  authMethod: AuthMethod;
  username?: string;
  password?: string;
  trustServerCertificate: boolean;
}

/**
 * Connection state stored in localStorage
 */
export interface ConnectionInfo {
  id: string;
  name: string;
  connectionString: string;
  connectedAt: string;
  server: string;
  database: string;
  provider: Provider;
}

/**
 * Test database templates
 */
export enum TestDatabaseTemplate {
  Northwind = 'northwind',
  AdventureWorksLite = 'adventureworks-lite',
  SimpleBlog = 'simple-blog'
}

/**
 * Test database creation progress
 */
export interface TestDatabaseProgress {
  stage: string;
  percent: number;
  message: string;
  connectionString?: string;
}

/**
 * Connection form validation errors
 */
export interface ConnectionFormErrors {
  server?: string;
  database?: string;
  username?: string;
  password?: string;
  general?: string;
}

/**
 * Connection state for UI
 */
export type ConnectionState =
  | 'idle'
  | 'validating'
  | 'connecting'
  | 'connected'
  | 'testing'
  | 'error';

/**
 * Component props
 */
export interface ConnectionFormProps {
  onConnect: (connectionString: string, connectionName: string) => void;
  onTestConnection?: (connectionString: string) => Promise<boolean>;
  isLoading?: boolean;
  error?: string | null;
}

export interface WelcomePanelProps {
  onConnectClick: () => void;
  onCreateTestDatabase: () => void;
  recentConnections: ConnectionInfo[];
  onSelectRecentConnection: (connection: ConnectionInfo) => void;
  onClearRecentConnections: () => void;
}

export interface TestDatabaseDialogProps {
  isOpen: boolean;
  onClose: () => void;
  onCreate: (template: TestDatabaseTemplate) => Promise<void>;
  onConnectAfterCreate?: (connectionString: string) => void;
  isCreating?: boolean;
  progress?: TestDatabaseProgress | null;
  error?: string | null;
}
