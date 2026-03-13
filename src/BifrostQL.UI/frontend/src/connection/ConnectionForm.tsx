import React, { useState, useCallback } from 'react';
import {
  Provider,
  PROVIDERS,
  AuthMethod,
  ConnectionFormProps,
  ConnectionFormErrors,
  ConnectionState,
  SqlServerFormData,
  PostgresFormData,
  MySqlFormData,
  SqliteFormData,
  PostgresSslMode,
  MySqlSslMode,
} from './types';

const POSTGRES_SSL_MODES: PostgresSslMode[] = ['Disable', 'Allow', 'Prefer', 'Require', 'VerifyCA', 'VerifyFull'];
const MYSQL_SSL_MODES: MySqlSslMode[] = ['None', 'Preferred', 'Required'];

function createDefaultFormData(provider: Provider) {
  switch (provider) {
    case 'sqlserver':
      return { server: 'localhost', database: '', authMethod: AuthMethod.SqlServer, username: '', password: '', trustServerCertificate: true } satisfies SqlServerFormData;
    case 'postgres':
      return { host: 'localhost', port: 5432, database: '', username: '', password: '', sslMode: 'Prefer' as PostgresSslMode } satisfies PostgresFormData;
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
      return `Host=${d.host};Port=${d.port};Database=${d.database};Username=${d.username};Password=${d.password};SSL Mode=${d.sslMode}`;
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
      if (!d.username.trim()) errors.username = 'Username is required';
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

  const handleConnect = useCallback(() => {
    if (!useRawString) {
      const fieldErrors = validateFormData(provider, formData);
      if (Object.values(fieldErrors).some(Boolean)) {
        setErrors(fieldErrors);
        setTouched(new Set(Object.keys(fieldErrors)));
        return;
      }
    }
    setConnectionState('connecting');
    const connString = getConnectionString();
    const connName = useRawString ? `${providerInfo.name} connection` : buildConnectionName(provider, formData);
    onConnect(connString, connName);
  }, [provider, formData, useRawString, getConnectionString, providerInfo, onConnect]);

  const hasErrors = Object.values(errors).some(Boolean);

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
            {renderField('database', 'Database Name', 'text', d.database, true, 'my_database')}

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
            {renderField('database', 'Database Name', 'text', d.database, true, 'my_database')}
            {renderField('username', 'Username', 'text', d.username, true, 'postgres')}
            {renderField('password', 'Password', 'password', d.password, false)}
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
            {renderField('database', 'Database Name', 'text', d.database, true, 'my_database')}
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
