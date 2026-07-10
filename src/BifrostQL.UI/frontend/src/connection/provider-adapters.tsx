import { ReactElement } from 'react';
import {
  Provider,
  AuthMethod,
  PostgresAuthMethod,
  ConnectionFormData,
  ConnectionFormErrors,
  ConnectionRequest,
  SqlServerFormData,
  PostgresFormData,
  MySqlFormData,
  SqliteFormData,
  PostgresSslMode,
  MySqlSslMode,
  SshConfig,
  WpConfig,
} from './types';

const POSTGRES_SSL_MODES: PostgresSslMode[] = ['Disable', 'Allow', 'Prefer', 'Require', 'VerifyCA', 'VerifyFull'];
const MYSQL_SSL_MODES: MySqlSslMode[] = ['None', 'Preferred', 'Required'];

/**
 * Per-field render helpers + form state a provider adapter needs to draw its
 * fields. Supplied by ConnectionForm so the adapters stay decoupled from the
 * component's local state (errors, disabled, updateField, ...).
 */
export interface ProviderFieldContext {
  data: ConnectionFormData;
  isDisabled: boolean;
  updateField: (field: string, value: unknown) => void;
  renderField: (
    id: string,
    label: string,
    type: string,
    value: string | number,
    required?: boolean,
    placeholder?: string,
  ) => ReactElement;
  renderSelect: (id: string, label: string, value: string, options: string[]) => ReactElement;
  renderDatabaseField: () => ReactElement | null;
}

/** Extra context validation needs beyond the raw form data. */
export interface ValidateContext {
  /** WordPress auto-discovery selected — relaxes the database/username requirement for MySQL. */
  wpDiscoveryEnabled: boolean;
}

/**
 * Strategy object grouping every per-provider behavior that used to live in a
 * parallel `switch (provider)`: form defaults, connection-string/name building,
 * validation, request assembly, and field rendering. Look up
 * `PROVIDER_ADAPTERS[provider]` once and call the method you need.
 */
export interface ProviderAdapter {
  /** Default server port for the provider (0 when N/A, e.g. file-based SQLite). */
  defaultPort: number;
  createDefaultFormData: () => ConnectionFormData;
  buildConnectionString: (data: ConnectionFormData) => string;
  buildConnectionName: (data: ConnectionFormData) => string;
  validateFormData: (data: ConnectionFormData, ctx: ValidateContext) => ConnectionFormErrors;
  buildConnectionRequest: (
    data: ConnectionFormData,
    name: string,
    sshConfig: SshConfig,
    wpConfig: WpConfig,
  ) => ConnectionRequest;
  renderFields: (ctx: ProviderFieldContext) => ReactElement;
}

// SSH tunnel applies to every server-based provider; only file-based SQLite opts out.
function tunnelFor(sshConfig: SshConfig): SshConfig | undefined {
  return sshConfig.enabled ? sshConfig : undefined;
}

const sqlServerAdapter: ProviderAdapter = {
  defaultPort: 1433,
  createDefaultFormData: () => ({
    server: 'localhost',
    database: '',
    authMethod: AuthMethod.SqlServer,
    username: '',
    trustServerCertificate: true,
  } satisfies SqlServerFormData),
  buildConnectionString: (data) => {
    const d = data as SqlServerFormData;
    const parts = [`Server=${d.server}`, `Database=${d.database}`];
    if (d.authMethod === AuthMethod.SqlServer) {
      parts.push(`User Id=${d.username}`);
    } else {
      parts.push('Integrated Security=true');
    }
    if (d.trustServerCertificate) {
      parts.push('TrustServerCertificate=True');
    }
    return parts.join(';');
  },
  buildConnectionName: (data) => {
    const d = data as SqlServerFormData;
    return `${d.server}/${d.database}`;
  },
  validateFormData: (data) => {
    const d = data as SqlServerFormData;
    const errors: ConnectionFormErrors = {};
    if (!d.server.trim()) errors.server = 'Server address is required';
    if (!d.database.trim()) errors.database = 'Database name is required';
    if (d.authMethod === AuthMethod.SqlServer) {
      if (!d.username.trim()) errors.username = 'Username is required';
    }
    return errors;
  },
  buildConnectionRequest: (data, name, sshConfig) => {
    const d = data as SqlServerFormData;
    return {
      name,
      provider: 'sqlserver',
      host: d.server,
      port: sqlServerAdapter.defaultPort,
      database: d.database,
      username: d.authMethod === AuthMethod.SqlServer ? d.username : undefined,
      ssl: d.trustServerCertificate,
      ssh: tunnelFor(sshConfig),
      tags: undefined,
      requiresCredential: d.authMethod === AuthMethod.SqlServer,
    };
  },
  renderFields: ({ data, isDisabled, updateField, renderField, renderDatabaseField }) => {
    const d = data as SqlServerFormData;
    return (
      <>
        {renderField('server', 'Server Address', 'text', d.server, true, 'localhost')}
        {renderDatabaseField()}

        <fieldset className="conn-form__fieldset">
          <legend className="conn-form__label">Authentication Method</legend>
          <div className="conn-form__radio-group">
            <label className="conn-form__radio-label">
              <input
                type="radio"
                name="authMethod"
                value={AuthMethod.SqlServer}
                checked={d.authMethod === AuthMethod.SqlServer}
                onChange={(e) => updateField('authMethod', e.target.value)}
                disabled={isDisabled}
              />
              SQL Server Authentication
            </label>
            <label className="conn-form__radio-label">
              <input
                type="radio"
                name="authMethod"
                value={AuthMethod.Windows}
                checked={d.authMethod === AuthMethod.Windows}
                onChange={(e) => updateField('authMethod', e.target.value)}
                disabled={isDisabled}
              />
              Windows Authentication
            </label>
          </div>
        </fieldset>

        {d.authMethod === AuthMethod.SqlServer && (
          <>
            {renderField('username', 'Username', 'text', d.username, true, 'sa')}
          </>
        )}

        <label className="conn-form__checkbox">
          <input
            type="checkbox"
            checked={d.trustServerCertificate}
            onChange={(e) => updateField('trustServerCertificate', e.target.checked)}
            disabled={isDisabled}
          />
          <span>Trust Server Certificate</span>
        </label>
      </>
    );
  },
};

const postgresAdapter: ProviderAdapter = {
  defaultPort: 5432,
  createDefaultFormData: () => ({
    host: 'localhost',
    port: 5432,
    database: '',
    authMethod: PostgresAuthMethod.Password,
    username: '',
    sslMode: 'Prefer' as PostgresSslMode,
  } satisfies PostgresFormData),
  buildConnectionString: (data) => {
    const d = data as PostgresFormData;
    const parts = [`Host=${d.host}`, `Port=${d.port}`, `Database=${d.database}`];
    if (d.authMethod === PostgresAuthMethod.Password) {
      parts.push(`Username=${d.username}`);
    }
    parts.push(`SSL Mode=${d.sslMode}`);
    return parts.join(';');
  },
  buildConnectionName: (data) => {
    const d = data as PostgresFormData;
    return `${d.host}:${d.port}/${d.database}`;
  },
  validateFormData: (data) => {
    const d = data as PostgresFormData;
    const errors: ConnectionFormErrors = {};
    if (!d.host.trim()) errors.host = 'Host is required';
    if (!d.database.trim()) errors.database = 'Database name is required';
    if (d.authMethod === PostgresAuthMethod.Password) {
      if (!d.username.trim()) errors.username = 'Username is required';
    }
    if (!d.port || d.port < 1 || d.port > 65535) errors.port = 'Valid port required (1-65535)';
    return errors;
  },
  buildConnectionRequest: (data, name, sshConfig) => {
    const d = data as PostgresFormData;
    return {
      name,
      provider: 'postgres',
      host: d.host,
      port: d.port,
      database: d.database,
      username: d.authMethod === PostgresAuthMethod.Password ? d.username : undefined,
      ssl: d.sslMode !== 'Disable',
      ssh: tunnelFor(sshConfig),
      tags: undefined,
      requiresCredential: d.authMethod === PostgresAuthMethod.Password,
    };
  },
  renderFields: ({ data, isDisabled, updateField, renderField, renderSelect, renderDatabaseField }) => {
    const d = data as PostgresFormData;
    return (
      <>
        {renderField('host', 'Host', 'text', d.host, true, 'localhost')}
        {renderField('port', 'Port', 'number', d.port, true, '5432')}

        <fieldset className="conn-form__fieldset">
          <legend className="conn-form__label">Authentication Method</legend>
          <div className="conn-form__radio-group">
            <label className="conn-form__radio-label">
              <input
                type="radio"
                name="pgAuthMethod"
                value={PostgresAuthMethod.Password}
                checked={d.authMethod === PostgresAuthMethod.Password}
                onChange={(e) => updateField('authMethod', e.target.value)}
                disabled={isDisabled}
              />
              Password Authentication
            </label>
            <label className="conn-form__radio-label">
              <input
                type="radio"
                name="pgAuthMethod"
                value={PostgresAuthMethod.Peer}
                checked={d.authMethod === PostgresAuthMethod.Peer}
                onChange={(e) => updateField('authMethod', e.target.value)}
                disabled={isDisabled}
              />
              Peer / Ident (passwordless)
            </label>
          </div>
        </fieldset>

        {d.authMethod === PostgresAuthMethod.Password && (
          <>
            {renderField('username', 'Username', 'text', d.username, true, 'postgres')}
          </>
        )}

        {renderDatabaseField()}
        {renderSelect('sslMode', 'SSL Mode', d.sslMode, POSTGRES_SSL_MODES)}
      </>
    );
  },
};

const mySqlAdapter: ProviderAdapter = {
  defaultPort: 3306,
  createDefaultFormData: () => ({
    host: 'localhost',
    port: 3306,
    database: '',
    username: '',
    sslMode: 'Preferred' as MySqlSslMode,
  } satisfies MySqlFormData),
  buildConnectionString: (data) => {
    const d = data as MySqlFormData;
    const parts = [`Server=${d.host}`, `Port=${d.port}`, `Database=${d.database}`, `Uid=${d.username}`];
    parts.push(`SslMode=${d.sslMode}`);
    return parts.join(';');
  },
  buildConnectionName: (data) => {
    const d = data as MySqlFormData;
    return `${d.host}:${d.port}/${d.database}`;
  },
  validateFormData: (data, { wpDiscoveryEnabled }) => {
    const d = data as MySqlFormData;
    const errors: ConnectionFormErrors = {};
    if (!d.host.trim()) errors.host = 'Host is required';
    if (!wpDiscoveryEnabled && !d.database.trim()) errors.database = 'Database name is required';
    if (!wpDiscoveryEnabled && !d.username.trim()) errors.username = 'Username is required';
    if (!d.port || d.port < 1 || d.port > 65535) errors.port = 'Valid port required (1-65535)';
    return errors;
  },
  buildConnectionRequest: (data, name, sshConfig, wpConfig) => {
    const d = data as MySqlFormData;
    return {
      name,
      provider: 'mysql',
      host: d.host,
      port: d.port,
      database: d.database,
      username: wpConfig.enabled ? undefined : d.username,
      ssl: d.sslMode !== 'None',
      ssh: tunnelFor(sshConfig),
      tags: wpConfig.enabled ? ['wordpress'] : undefined,
      requiresCredential: !wpConfig.enabled,
    };
  },
  renderFields: ({ data, renderField, renderSelect, renderDatabaseField }) => {
    const d = data as MySqlFormData;
    return (
      <>
        {renderField('host', 'Host', 'text', d.host, true, 'localhost')}
        {renderField('port', 'Port', 'number', d.port, true, '3306')}
        {renderDatabaseField()}
        {renderField('username', 'Username', 'text', d.username, true, 'root')}
        {renderSelect('sslMode', 'SSL Mode', d.sslMode, MYSQL_SSL_MODES)}
      </>
    );
  },
};

const sqliteAdapter: ProviderAdapter = {
  defaultPort: 0,
  createDefaultFormData: () => ({ filePath: '', createNew: false } satisfies SqliteFormData),
  buildConnectionString: (data) => {
    const d = data as SqliteFormData;
    return `Data Source=${d.filePath}`;
  },
  buildConnectionName: (data) => {
    const d = data as SqliteFormData;
    return d.filePath.split(/[/\\]/).pop() || d.filePath;
  },
  validateFormData: (data) => {
    const d = data as SqliteFormData;
    const errors: ConnectionFormErrors = {};
    if (!d.filePath.trim()) errors.filePath = 'File path is required';
    return errors;
  },
  buildConnectionRequest: (data, name) => {
    const d = data as SqliteFormData;
    return {
      name,
      provider: 'sqlite',
      connectionString: sqliteAdapter.buildConnectionString(d),
      database: d.filePath,
      requiresCredential: false,
    };
  },
  renderFields: ({ data, isDisabled, updateField, renderField }) => {
    const d = data as SqliteFormData;
    return (
      <>
        {renderField('filePath', 'Database File Path', 'text', d.filePath, true, '/path/to/database.db')}
        <label className="conn-form__checkbox">
          <input
            type="checkbox"
            checked={d.createNew}
            onChange={(e) => updateField('createNew', e.target.checked)}
            disabled={isDisabled}
          />
          <span>Create new database at this path</span>
        </label>
      </>
    );
  },
};

export const PROVIDER_ADAPTERS: Record<Provider, ProviderAdapter> = {
  sqlserver: sqlServerAdapter,
  postgres: postgresAdapter,
  mysql: mySqlAdapter,
  sqlite: sqliteAdapter,
};
