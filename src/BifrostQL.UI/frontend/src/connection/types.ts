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
