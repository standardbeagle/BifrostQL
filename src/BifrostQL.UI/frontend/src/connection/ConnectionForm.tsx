import React, { useState, useCallback } from 'react';
import {
  Provider,
  PROVIDERS,
  AuthMethod,
  PostgresAuthMethod,
  ConnectionFormProps,
  ConnectionFormErrors,
  ConnectionState,
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

const DEFAULT_SSH: SshConfig = { enabled: false, sshHost: '', sshPort: 22, sshUsername: '', identityFile: '' };
const DEFAULT_WP: WpConfig = { enabled: false, wpPath: 'wp', wpRoot: '' };

function getDefaultDbPort(provider: Provider): number {
  switch (provider) {
    case 'sqlserver': return 1433;
    case 'postgres': return 5432;
    case 'mysql': return 3306;
    default: return 0;
  }
}

function rewriteForTunnel(provider: Provider, connStr: string, localPort: number): string {
  const parts = new Map<string, string>();
  for (const segment of connStr.split(';')) {
    const eq = segment.indexOf('=');
    if (eq <= 0) continue;
    parts.set(segment.slice(0, eq).trim().toLowerCase(), segment.slice(eq + 1).trim());
  }
  switch (provider) {
    case 'sqlserver':
      parts.set('server', `127.0.0.1,${localPort}`);
      break;
    case 'postgres':
      parts.set('host', '127.0.0.1');
      parts.set('port', String(localPort));
      break;
    case 'mysql':
      parts.set('server', '127.0.0.1');
      parts.set('port', String(localPort));
      break;
  }
  return [...parts.entries()].map(([k, v]) => `${k}=${v}`).join(';');
}

function createDefaultFormData(provider: Provider) {
  switch (provider) {
    case 'sqlserver':
      return { server: 'localhost', database: '', authMethod: AuthMethod.SqlServer, username: '', password: '', trustServerCertificate: true } satisfies SqlServerFormData;
    case 'postgres':
      return { host: 'localhost', port: 5432, database: '', authMethod: PostgresAuthMethod.Password, username: '', password: '', sslMode: 'Prefer' as PostgresSslMode } satisfies PostgresFormData;
    case 'mysql':
      return { host: 'localhost', port: 3306, database: '', username: '', password: '', sslMode: 'Preferred' as MySqlSslMode } satisfies MySqlFormData;
    case 'sqlite':
      return { filePath: '', createNew: false } satisfies SqliteFormData;
  }
}

function buildConnectionString(provider: Provider, data: Record<string, any>): string {
  switch (provider) {
    case 'sqlserver': {
      const d = data as SqlServerFormData;
      const parts = [`Server=${d.server}`, `Database=${d.database}`];
      if (d.authMethod === AuthMethod.SqlServer) {
        parts.push(`User Id=${d.username}`, `Password=${d.password}`);
      } else {
        parts.push('Integrated Security=true');
      }
      if (d.trustServerCertificate) {
        parts.push('TrustServerCertificate=True');
      }
      return parts.join(';');
    }
    case 'postgres': {
      const d = data as PostgresFormData;
      const parts = [`Host=${d.host}`, `Port=${d.port}`, `Database=${d.database}`];
      if (d.authMethod === PostgresAuthMethod.Password) {
        parts.push(`Username=${d.username}`, `Password=${d.password}`);
      }
      parts.push(`SSL Mode=${d.sslMode}`);
      return parts.join(';');
    }
    case 'mysql': {
      const d = data as MySqlFormData;
      return `Server=${d.host};Port=${d.port};Database=${d.database};Uid=${d.username};Pwd=${d.password};SslMode=${d.sslMode}`;
    }
    case 'sqlite': {
      const d = data as SqliteFormData;
      return `Data Source=${d.filePath}`;
    }
  }
}

function buildConnectionName(provider: Provider, data: Record<string, any>): string {
  switch (provider) {
    case 'sqlserver': {
      const d = data as SqlServerFormData;
      return `${d.server}/${d.database}`;
    }
    case 'postgres':
    case 'mysql': {
      const d = data as PostgresFormData | MySqlFormData;
      return `${d.host}:${d.port}/${d.database}`;
    }
    case 'sqlite': {
      const d = data as SqliteFormData;
      return d.filePath.split(/[/\\]/).pop() || d.filePath;
    }
  }
}

function validateFormData(provider: Provider, data: Record<string, any>): ConnectionFormErrors {
  const errors: ConnectionFormErrors = {};

  switch (provider) {
    case 'sqlserver': {
      const d = data as SqlServerFormData;
      if (!d.server.trim()) errors.server = 'Server address is required';
      if (!d.database.trim()) errors.database = 'Database name is required';
      if (d.authMethod === AuthMethod.SqlServer) {
        if (!d.username.trim()) errors.username = 'Username is required';
        if (!d.password) errors.password = 'Password is required';
      }
      break;
    }
    case 'postgres': {
      const d = data as PostgresFormData;
      if (!d.host.trim()) errors.host = 'Host is required';
      if (!d.database.trim()) errors.database = 'Database name is required';
      if (d.authMethod === PostgresAuthMethod.Password) {
        if (!d.username.trim()) errors.username = 'Username is required';
      }
      if (!d.port || d.port < 1 || d.port > 65535) errors.port = 'Valid port required (1-65535)';
      break;
    }
    case 'mysql': {
      const d = data as MySqlFormData;
      if (!d.host.trim()) errors.host = 'Host is required';
      if (!d.database.trim()) errors.database = 'Database name is required';
      if (!d.username.trim()) errors.username = 'Username is required';
      if (!d.port || d.port < 1 || d.port > 65535) errors.port = 'Valid port required (1-65535)';
      break;
    }
    case 'sqlite': {
      const d = data as SqliteFormData;
      if (!d.filePath.trim()) errors.filePath = 'File path is required';
      break;
    }
  }

  return errors;
}

export const ConnectionForm: React.FC<ConnectionFormProps> = ({
  provider,
  onConnect,
  onTestConnection,
  onBack,
}) => {
  const providerInfo = PROVIDERS.find((p) => p.id === provider)!;
  const [formData, setFormData] = useState<Record<string, any>>(() => createDefaultFormData(provider));
  const [errors, setErrors] = useState<ConnectionFormErrors>({});
  const [touched, setTouched] = useState<Set<string>>(new Set());
  const [connectionState, setConnectionState] = useState<ConnectionState>('idle');
  const [testResult, setTestResult] = useState<{ success: boolean; message: string } | null>(null);
  const [useRawString, setUseRawString] = useState(false);
  const [rawConnectionString, setRawConnectionString] = useState('');
  const [availableDatabases, setAvailableDatabases] = useState<string[] | null>(null);
  const [loadingDatabases, setLoadingDatabases] = useState(false);
  const [sshConfig, setSshConfig] = useState<SshConfig>(DEFAULT_SSH);
  const [wpConfig, setWpConfig] = useState<WpConfig>(DEFAULT_WP);
  const [tunnelPort, setTunnelPort] = useState<number | null>(null);
  const [wpDiscovering, setWpDiscovering] = useState(false);
  const [sshError, setSshError] = useState<string | null>(null);

  const isDisabled = connectionState === 'connecting' || connectionState === 'testing';

  const updateField = useCallback((field: string, value: unknown) => {
    setFormData((prev) => ({ ...prev, [field]: value }));
    setTestResult(null);
    if (touched.has(field)) {
      setErrors((prev) => {
        const updated = { ...prev };
        delete updated[field];
        return updated;
      });
    }
  }, [touched]);

  const handleBlur = useCallback((field: string) => {
    setTouched((prev) => new Set(prev).add(field));
    const fieldErrors = validateFormData(provider, formData);
    setErrors((prev) => ({ ...prev, [field]: fieldErrors[field] }));
  }, [provider, formData]);

  const getConnectionString = useCallback((): string => {
    if (useRawString) return rawConnectionString;
    return buildConnectionString(provider, formData);
  }, [useRawString, rawConnectionString, provider, formData]);

  const handleTestConnection = useCallback(async () => {
    if (!useRawString) {
      const fieldErrors = validateFormData(provider, formData);
      if (Object.values(fieldErrors).some(Boolean)) {
        setErrors(fieldErrors);
        setTouched(new Set(Object.keys(fieldErrors)));
        return;
      }
    }
    if (!onTestConnection) return;

    setConnectionState('testing');
    setTestResult(null);
    try {
      const success = await onTestConnection(getConnectionString());
      setTestResult({
        success,
        message: success
          ? 'Connection successful!'
          : 'Connection failed. Check your settings and try again.',
      });
    } catch (error) {
      setTestResult({
        success: false,
        message: error instanceof Error ? error.message : 'An unexpected error occurred',
      });
    } finally {
      setConnectionState('idle');
    }
  }, [provider, formData, useRawString, onTestConnection, getConnectionString]);

  const handleConnect = useCallback(async () => {
    if (!useRawString) {
      const fieldErrors = validateFormData(provider, formData);
      if (Object.values(fieldErrors).some(Boolean)) {
        setErrors(fieldErrors);
        setTouched(new Set(Object.keys(fieldErrors)));
        return;
      }
    }
    setConnectionState('connecting');
    setSshError(null);

    let connString = getConnectionString();

    // Start SSH tunnel if enabled
    if (sshConfig.enabled && provider !== 'sqlite') {
      try {
        const dbHost = (formData as any).host || (formData as any).server || 'localhost';
        const dbPort = (formData as any).port || getDefaultDbPort(provider);
        const resp = await fetch('/api/ssh/connect', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            sshHost: sshConfig.sshHost,
            sshPort: sshConfig.sshPort,
            sshUsername: sshConfig.sshUsername,
            identityFile: sshConfig.identityFile || null,
            remoteHost: dbHost,
            remotePort: dbPort,
          }),
        });
        const result = await resp.json();
        if (!resp.ok || !result.success) {
          setSshError(result.error || 'Failed to start SSH tunnel');
          setConnectionState('idle');
          return;
        }
        setTunnelPort(result.localPort);
        connString = rewriteForTunnel(provider, connString, result.localPort);
      } catch (err) {
        setSshError(err instanceof Error ? err.message : 'SSH tunnel failed');
        setConnectionState('idle');
        return;
      }
    }

    const connName = useRawString ? `${providerInfo.name} connection` : buildConnectionName(provider, formData);
    onConnect(connString, connName);
  }, [provider, formData, useRawString, getConnectionString, providerInfo, onConnect, sshConfig]);

  const updateSshField = useCallback((field: string, value: unknown) => {
    setSshConfig((prev) => ({ ...prev, [field]: value }));
    setSshError(null);
  }, []);

  const handleWpDiscover = useCallback(async () => {
    if (!sshConfig.sshHost || !sshConfig.sshUsername) return;
    setWpDiscovering(true);
    setSshError(null);
    try {
      const resp = await fetch('/api/ssh/wp-discover', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          sshHost: sshConfig.sshHost,
          sshPort: sshConfig.sshPort,
          sshUsername: sshConfig.sshUsername,
          identityFile: sshConfig.identityFile || null,
          wpPath: wpConfig.wpPath || null,
          wpRoot: wpConfig.wpRoot || null,
        }),
      });
      const result = await resp.json();
      if (!resp.ok || !result.success) {
        setSshError(result.error || 'WP-CLI discovery failed');
        return;
      }
      // Auto-fill MySQL form fields from discovered credentials
      const host = result.dbHost?.includes(':') ? result.dbHost.split(':')[0] : (result.dbHost || 'localhost');
      const port = result.dbHost?.includes(':') ? Number(result.dbHost.split(':')[1]) : 3306;
      setFormData((prev) => ({
        ...prev,
        host,
        port,
        database: result.dbName,
        username: result.dbUser,
        password: result.dbPassword,
      }));
      setTestResult({ success: true, message: `Discovered WordPress database: ${result.dbName}` });
    } catch (err) {
      setSshError(err instanceof Error ? err.message : 'WP-CLI discovery failed');
    } finally {
      setWpDiscovering(false);
    }
  }, [sshConfig, wpConfig]);

  const handleLoadDatabases = useCallback(async () => {
    if (provider === 'sqlite') return;
    setLoadingDatabases(true);
    setAvailableDatabases(null);
    try {
      const partialData = { ...formData, database: 'master' };
      const connString = buildConnectionString(provider, partialData);
      const isPeerAuth = provider === 'postgres'
        && (formData as PostgresFormData).authMethod === PostgresAuthMethod.Peer;
      const response = await fetch('/api/databases', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          connectionString: connString,
          provider,
          peerAuth: isPeerAuth,
          psqlUser: isPeerAuth ? 'postgres' : null,
        }),
      });
      if (response.ok) {
        const result = await response.json();
        if (result.databases?.length > 0) {
          setAvailableDatabases(result.databases);
          if (!formData.database && result.databases.length > 0) {
            updateField('database', result.databases[0]);
          }
        }
      }
    } catch {
      // fall back to text input
    } finally {
      setLoadingDatabases(false);
    }
  }, [provider, formData, updateField]);

  const hasErrors = Object.values(errors).some(Boolean);

  const renderDatabaseField = () => {
    if (provider === 'sqlite') return null;
    const dbValue = (formData as Record<string, any>).database || '';

    return (
      <div className="conn-form__group">
        <label htmlFor="database" className="conn-form__label">
          Database Name <span className="conn-form__required">*</span>
        </label>
        <div className="conn-form__database-row">
          {availableDatabases ? (
            <select
              id="database"
              value={dbValue}
              onChange={(e) => updateField('database', e.target.value)}
              disabled={isDisabled}
              className="conn-form__select"
            >
              <option value="">-- Select a database --</option>
              {availableDatabases.map((db) => (
                <option key={db} value={db}>{db}</option>
              ))}
            </select>
          ) : (
            <input
              id="database"
              type="text"
              value={dbValue}
              onChange={(e) => updateField('database', e.target.value)}
              onBlur={() => handleBlur('database')}
              disabled={isDisabled}
              placeholder="my_database"
              className={`conn-form__input ${errors.database ? 'conn-form__input--error' : ''}`}
              aria-required
              aria-invalid={!!errors.database}
            />
          )}
          <button
            type="button"
            onClick={handleLoadDatabases}
            disabled={isDisabled || loadingDatabases}
            className="conn-form__btn conn-form__btn--icon"
            title="Load databases from server"
          >
            {loadingDatabases ? '...' : '\u21BB'}
          </button>
        </div>
        {errors.database && <span className="conn-form__error" role="alert">{errors.database}</span>}
        {availableDatabases && (
          <button
            type="button"
            className="conn-form__link-btn"
            onClick={() => { setAvailableDatabases(null); updateField('database', ''); }}
          >
            Type name manually
          </button>
        )}
      </div>
    );
  };

  const renderField = (
    id: string,
    label: string,
    type: string,
    value: string | number,
    required = true,
    placeholder?: string,
  ) => (
    <div className="conn-form__group">
      <label htmlFor={id} className="conn-form__label">
        {label} {required && <span className="conn-form__required">*</span>}
      </label>
      <input
        id={id}
        type={type}
        value={value}
        onChange={(e) => updateField(id, type === 'number' ? Number(e.target.value) : e.target.value)}
        onBlur={() => handleBlur(id)}
        disabled={isDisabled}
        placeholder={placeholder}
        className={`conn-form__input ${errors[id] ? 'conn-form__input--error' : ''}`}
        aria-required={required}
        aria-invalid={!!errors[id]}
      />
      {errors[id] && <span className="conn-form__error" role="alert">{errors[id]}</span>}
    </div>
  );

  const renderSelect = (id: string, label: string, value: string, options: string[]) => (
    <div className="conn-form__group">
      <label htmlFor={id} className="conn-form__label">{label}</label>
      <select
        id={id}
        value={value}
        onChange={(e) => updateField(id, e.target.value)}
        disabled={isDisabled}
        className="conn-form__select"
      >
        {options.map((opt) => (
          <option key={opt} value={opt}>{opt}</option>
        ))}
      </select>
    </div>
  );

  const renderProviderFields = () => {
    const data = formData as Record<string, any>;

    switch (provider) {
      case 'sqlserver': {
        const d = data as unknown as SqlServerFormData;
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
                {renderField('password', 'Password', 'password', d.password, true)}
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
      }

      case 'postgres': {
        const d = data as unknown as PostgresFormData;
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
                {renderField('password', 'Password', 'password', d.password, false)}
              </>
            )}

            {renderDatabaseField()}
            {renderSelect('sslMode', 'SSL Mode', d.sslMode, POSTGRES_SSL_MODES)}
          </>
        );
      }

      case 'mysql': {
        const d = data as unknown as MySqlFormData;
        return (
          <>
            {renderField('host', 'Host', 'text', d.host, true, 'localhost')}
            {renderField('port', 'Port', 'number', d.port, true, '3306')}
            {renderDatabaseField()}
            {renderField('username', 'Username', 'text', d.username, true, 'root')}
            {renderField('password', 'Password', 'password', d.password, false)}
            {renderSelect('sslMode', 'SSL Mode', d.sslMode, MYSQL_SSL_MODES)}
          </>
        );
      }

      case 'sqlite': {
        const d = data as unknown as SqliteFormData;
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
      }
    }
  };

  return (
    <div className="conn-form" role="form" aria-label={`${providerInfo.name} connection form`}>
      <button type="button" className="conn-form__back" onClick={onBack}>
        &larr; Back
      </button>

      <div className="conn-form__header">
        <span className="conn-form__provider-icon">{providerInfo.icon}</span>
        <h2 className="conn-form__title">Connect to {providerInfo.name}</h2>
      </div>

      {testResult && (
        <div
          className={`conn-form__alert ${testResult.success ? 'conn-form__alert--success' : 'conn-form__alert--error'}`}
          role="alert"
        >
          {testResult.success ? '\u2713' : '\u26A0'} {testResult.message}
        </div>
      )}

      {!useRawString && renderProviderFields()}

      {provider !== 'sqlite' && (
        <fieldset className="conn-form__ssh-section">
          <label className="conn-form__checkbox">
            <input
              type="checkbox"
              checked={sshConfig.enabled}
              onChange={(e) => updateSshField('enabled', e.target.checked)}
              disabled={isDisabled}
            />
            <span>Connect via SSH tunnel</span>
          </label>

          {sshConfig.enabled && (
            <div className="conn-form__ssh-fields">
              <div className="conn-form__group">
                <label htmlFor="sshHost" className="conn-form__label">
                  SSH Host <span className="conn-form__required">*</span>
                </label>
                <input id="sshHost" type="text" value={sshConfig.sshHost}
                  onChange={(e) => updateSshField('sshHost', e.target.value)}
                  disabled={isDisabled} placeholder="example.com"
                  className="conn-form__input" />
              </div>
              <div className="conn-form__row">
                <div className="conn-form__group conn-form__group--half">
                  <label htmlFor="sshPort" className="conn-form__label">SSH Port</label>
                  <input id="sshPort" type="number" value={sshConfig.sshPort}
                    onChange={(e) => updateSshField('sshPort', Number(e.target.value))}
                    disabled={isDisabled} placeholder="22"
                    className="conn-form__input" />
                </div>
                <div className="conn-form__group conn-form__group--half">
                  <label htmlFor="sshUsername" className="conn-form__label">
                    SSH Username <span className="conn-form__required">*</span>
                  </label>
                  <input id="sshUsername" type="text" value={sshConfig.sshUsername}
                    onChange={(e) => updateSshField('sshUsername', e.target.value)}
                    disabled={isDisabled} placeholder="deploy"
                    className="conn-form__input" />
                </div>
              </div>
              <div className="conn-form__group">
                <label htmlFor="identityFile" className="conn-form__label">Identity File (optional)</label>
                <input id="identityFile" type="text" value={sshConfig.identityFile}
                  onChange={(e) => updateSshField('identityFile', e.target.value)}
                  disabled={isDisabled} placeholder="~/.ssh/id_rsa (leave empty for SSH agent)"
                  className="conn-form__input" />
              </div>

              {provider === 'mysql' && (
                <div className="conn-form__wp-section">
                  <label className="conn-form__checkbox">
                    <input
                      type="checkbox"
                      checked={wpConfig.enabled}
                      onChange={(e) => setWpConfig((prev) => ({ ...prev, enabled: e.target.checked }))}
                      disabled={isDisabled}
                    />
                    <span>WordPress auto-discover (wp-cli)</span>
                  </label>

                  {wpConfig.enabled && (
                    <>
                      <div className="conn-form__row">
                        <div className="conn-form__group conn-form__group--half">
                          <label htmlFor="wpPath" className="conn-form__label">WP-CLI Path</label>
                          <input id="wpPath" type="text" value={wpConfig.wpPath}
                            onChange={(e) => setWpConfig((prev) => ({ ...prev, wpPath: e.target.value }))}
                            disabled={isDisabled} placeholder="wp"
                            className="conn-form__input" />
                        </div>
                        <div className="conn-form__group conn-form__group--half">
                          <label htmlFor="wpRoot" className="conn-form__label">WordPress Root</label>
                          <input id="wpRoot" type="text" value={wpConfig.wpRoot}
                            onChange={(e) => setWpConfig((prev) => ({ ...prev, wpRoot: e.target.value }))}
                            disabled={isDisabled} placeholder="/var/www/html (auto-detect)"
                            className="conn-form__input" />
                        </div>
                      </div>
                      <button
                        type="button"
                        onClick={handleWpDiscover}
                        disabled={isDisabled || wpDiscovering || !sshConfig.sshHost || !sshConfig.sshUsername}
                        className="conn-form__btn conn-form__btn--secondary"
                      >
                        {wpDiscovering ? 'Discovering...' : 'Discover Credentials'}
                      </button>
                    </>
                  )}
                </div>
              )}

              {tunnelPort && (
                <div className="conn-form__alert conn-form__alert--success" role="status">
                  {'\u2713'} SSH tunnel active on local port {tunnelPort}
                </div>
              )}
              {sshError && (
                <div className="conn-form__alert conn-form__alert--error" role="alert">
                  {'\u26A0'} {sshError}
                </div>
              )}
            </div>
          )}
        </fieldset>
      )}

      <label className="conn-form__checkbox conn-form__raw-toggle">
        <input
          type="checkbox"
          checked={useRawString}
          onChange={(e) => setUseRawString(e.target.checked)}
          disabled={isDisabled}
        />
        <span>Use connection string</span>
      </label>

      {useRawString && (
        <div className="conn-form__group">
          <label htmlFor="rawConnectionString" className="conn-form__label">Connection String</label>
          <textarea
            id="rawConnectionString"
            value={rawConnectionString}
            onChange={(e) => { setRawConnectionString(e.target.value); setTestResult(null); }}
            disabled={isDisabled}
            className="conn-form__textarea"
            rows={3}
            placeholder="Enter your full connection string..."
          />
        </div>
      )}

      <div className="conn-form__buttons">
        <button
          type="button"
          onClick={handleTestConnection}
          disabled={isDisabled || (!useRawString && hasErrors) || !onTestConnection}
          className="conn-form__btn conn-form__btn--secondary"
          aria-busy={connectionState === 'testing'}
        >
          {connectionState === 'testing' && <span className="conn-form__spinner" aria-hidden="true" />}
          Test Connection
        </button>
        <button
          type="button"
          onClick={handleConnect}
          disabled={isDisabled || (!useRawString && hasErrors)}
          className="conn-form__btn conn-form__btn--primary"
          aria-busy={connectionState === 'connecting'}
        >
          {connectionState === 'connecting' && <span className="conn-form__spinner" aria-hidden="true" />}
          Connect
        </button>
      </div>
    </div>
  );
};

export default ConnectionForm;
