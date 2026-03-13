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
 * SSL mode options for PostgreSQL
 */
export type PostgresSslMode = 'Disable' | 'Allow' | 'Prefer' | 'Require' | 'VerifyCA' | 'VerifyFull';

/**
 * SSL mode options for MySQL
 */
export type MySqlSslMode = 'None' | 'Preferred' | 'Required';

/**
 * Per-provider connection form data
 */
export interface SqlServerFormData {
  server: string;
  database: string;
  authMethod: AuthMethod;
  username: string;
  password: string;
  trustServerCertificate: boolean;
}

export interface PostgresFormData {
  host: string;
  port: number;
  database: string;
  username: string;
  password: string;
  sslMode: PostgresSslMode;
}

export interface MySqlFormData {
  host: string;
  port: number;
  database: string;
  username: string;
  password: string;
  sslMode: MySqlSslMode;
}

export interface SqliteFormData {
  filePath: string;
  createNew: boolean;
}

/**
 * Union of all provider form data
 */
export type ConnectionFormData = SqlServerFormData | PostgresFormData | MySqlFormData | SqliteFormData;

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
 * Connection form validation errors (keyed by field name)
 */
export interface ConnectionFormErrors {
  [field: string]: string | undefined;
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
  provider: Provider;
  onConnect: (connectionString: string, connectionName: string) => void;
  onTestConnection?: (connectionString: string) => Promise<boolean>;
  onBack: () => void;
}

export interface ProviderSelectProps {
  onProviderSelect: (provider: Provider) => void;
  onBack: () => void;
}

export interface WelcomePanelProps {
  onConnectClick: () => void;
  onCreateTestDatabase: () => void;
  recentConnections: ConnectionInfo[];
  onSelectRecentConnection: (connection: ConnectionInfo) => void;
  onClearRecentConnections: () => void;
}

export interface QuickStartProps {
  onLaunch: (schema: QuickStartSchema, dataSize: DataSize) => void;
  onBack: () => void;
  isLaunching: boolean;
  launchProgress: string;
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
