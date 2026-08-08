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
 * Authentication methods for PostgreSQL connection
 */
export enum PostgresAuthMethod {
  Password = 'password',
  Peer = 'peer'
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
  trustServerCertificate: boolean;
}

export interface PostgresFormData {
  host: string;
  port: number;
  database: string;
  authMethod: PostgresAuthMethod;
  username: string;
  sslMode: PostgresSslMode;
}

export interface MySqlFormData {
  host: string;
  port: number;
  database: string;
  username: string;
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
 * SSH tunnel configuration
 */
export interface SshConfig {
  enabled: boolean;
  sshHost: string;
  sshPort: number;
  sshUsername: string;
  identityFile: string;
}

/**
 * WordPress WP-CLI discovery configuration
 */
export interface WpConfig {
  enabled: boolean;
  wpPath: string;
  wpRoot: string;
}

/**
 * Persisted connection state — the active session lives in sessionStorage
 * (connection/session.ts); the recent-connections list lives in localStorage
 * (connection/recent-connections.ts).
 */
export interface ConnectionInfo {
  id: string;
  name: string;
  connectionString: string;
  connectedAt: string;
  server: string;
  database: string;
  provider: Provider;
  ssh?: SshConfig;
  /** Set when connected via the credential vault — used to restore the connection after a backend restart */
  vaultServerName?: string;
}

export interface ConnectionRequest {
  name: string;
  provider: Provider;
  connectionString?: string;
  host?: string;
  port?: number;
  database?: string;
  username?: string;
  ssl?: boolean;
  ssh?: SshConfig;
  tags?: string[];
  requiresCredential?: boolean;
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
  /**
   * Perform the connection. The form awaits the result so it can restore an
   * interactive state on failure — a `void`-returning handler would leave the
   * form disabled forever whenever the attempt did not succeed.
   */
  onConnect: (request: ConnectionRequest) => void | Promise<void>;
  onTestConnection?: (request: ConnectionRequest) => Promise<boolean>;
  onBack: () => void;
}

export interface ProviderSelectProps {
  onProviderSelect: (provider: Provider) => void;
  onBack: () => void;
}

/**
 * A server from the encrypted credential vault (metadata only, no passwords)
 */
export interface VaultServer {
  name: string;
  provider: Provider;
  host: string;
  port: number;
  database?: string;
  tags: string[];
  hasSsh: boolean;
  hasPassword: boolean;
  source: 'vault' | 'env';
}

export interface WelcomePanelProps {
  onConnectClick: () => void;
  onQuickStart: () => void;
  recentConnections: ConnectionInfo[];
  onSelectRecentConnection: (connection: ConnectionInfo) => void;
  onClearRecentConnections: () => void;
  vaultServers?: VaultServer[];
  onConnectVaultServer?: (name: string) => void;
}

export interface QuickStartProps {
  onLaunch: (schema: QuickStartSchema, dataSize: DataSize) => void;
  onBack: () => void;
  isLaunching: boolean;
  launchProgress: string;
}
