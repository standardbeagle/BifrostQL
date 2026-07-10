import React, { useState, useCallback } from 'react';
import {
  Provider,
  PROVIDERS,
  AuthMethod,
  PostgresAuthMethod,
  ConnectionFormProps,
  ConnectionFormErrors,
  ConnectionFormData,
  ConnectionRequest,
  ConnectionState,
  SqlServerFormData,
  PostgresFormData,
  SshConfig,
  WpConfig,
} from './types';
import { parseAdoConnectionString } from './sanitize-connection';
import { PROVIDER_ADAPTERS } from './provider-adapters';

const DEFAULT_SSH: SshConfig = { enabled: false, sshHost: '', sshPort: 22, sshUsername: '', identityFile: '' };
const DEFAULT_WP: WpConfig = { enabled: false, wpPath: 'wp', wpRoot: '' };

/**
 * Build a ConnectionRequest from a raw ADO.NET connection string. Only the
 * SQLite path reaches this today (raw-string mode); the non-secret fields are
 * parsed by the shared {@link parseAdoConnectionString} so this and the App-side
 * bridge parse stay in lockstep.
 */
function parseConnectionString(provider: Provider, connectionString: string): ConnectionRequest {
  const { host, port, database, username, ssl } = parseAdoConnectionString(connectionString, provider);

  const name = provider === 'sqlite'
    ? host ?? `${provider} connection`
    : `${host ?? 'server'}${port ? `:${port}` : ''}/${database ?? ''}`;

  return {
    name,
    provider,
    connectionString: provider === 'sqlite' ? connectionString : undefined,
    host,
    port,
    database,
    username,
    ssl: ssl ?? false,
  };
}

export const ConnectionForm: React.FC<ConnectionFormProps> = ({
  provider,
  onConnect,
  onTestConnection,
  onBack,
}) => {
  const providerInfo = PROVIDERS.find((p) => p.id === provider)!;
  const adapter = PROVIDER_ADAPTERS[provider];
  const [formData, setFormData] = useState<ConnectionFormData>(() => adapter.createDefaultFormData());
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
  const [sshError, setSshError] = useState<string | null>(null);

  const isDisabled = connectionState === 'connecting' || connectionState === 'testing';

  const updateField = useCallback((field: string, value: unknown) => {
    setFormData((prev) => ({ ...prev, [field]: value } as ConnectionFormData));
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
    const fieldErrors = adapter.validateFormData(formData, { wpDiscoveryEnabled: false });
    setErrors((prev) => ({ ...prev, [field]: fieldErrors[field] }));
  }, [adapter, formData]);

  const handleTestConnection = useCallback(async () => {
    if (!useRawString) {
      const fieldErrors = adapter.validateFormData(formData, { wpDiscoveryEnabled: wpConfig.enabled });
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
      const connName = useRawString ? `${providerInfo.name} connection` : adapter.buildConnectionName(formData);
      const request = useRawString && provider === 'sqlite'
        ? { ...parseConnectionString(provider, rawConnectionString), name: connName }
        : adapter.buildConnectionRequest(formData, connName, sshConfig, wpConfig);
      const success = await onTestConnection(request);
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
  }, [adapter, provider, formData, useRawString, onTestConnection, providerInfo, rawConnectionString, sshConfig, wpConfig]);

  const handleConnect = useCallback(async () => {
    if (!useRawString) {
      const fieldErrors = adapter.validateFormData(formData, { wpDiscoveryEnabled: wpConfig.enabled });
      if (Object.values(fieldErrors).some(Boolean)) {
        setErrors(fieldErrors);
        setTouched(new Set(Object.keys(fieldErrors)));
        return;
      }
    }
    setConnectionState('connecting');
    setSshError(null);

    if (sshConfig.enabled && provider !== 'sqlite') {
      if (!sshConfig.sshHost.trim() || !sshConfig.sshUsername.trim()) {
        setSshError('SSH host and username are required');
        setConnectionState('idle');
        return;
      }
    }

    const connName = useRawString ? `${providerInfo.name} connection` : adapter.buildConnectionName(formData);
    const request = useRawString && provider === 'sqlite'
      ? { ...parseConnectionString(provider, rawConnectionString), name: connName }
      : adapter.buildConnectionRequest(formData, connName, sshConfig, wpConfig);
    onConnect(request);
  }, [adapter, provider, formData, useRawString, rawConnectionString, providerInfo, onConnect, sshConfig, wpConfig]);

  const updateSshField = useCallback((field: string, value: unknown) => {
    setSshConfig((prev) => ({ ...prev, [field]: value }));
    setSshError(null);
  }, []);

  const handleLoadDatabases = useCallback(async () => {
    if (provider === 'sqlite') return;
    const passwordAuth = (provider === 'sqlserver' && (formData as SqlServerFormData).authMethod === AuthMethod.SqlServer)
      || (provider === 'postgres' && (formData as PostgresFormData).authMethod === PostgresAuthMethod.Password)
      || provider === 'mysql';
    if (passwordAuth) {
      setTestResult({
        success: false,
        message: 'Database discovery is available after connecting with the vault credential flow.',
      });
      return;
    }
    setLoadingDatabases(true);
    setAvailableDatabases(null);
    try {
      const partialData = { ...formData, database: 'master' } as ConnectionFormData;
      const connString = adapter.buildConnectionString(partialData);
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
          if (!(formData as { database?: string }).database && result.databases.length > 0) {
            updateField('database', result.databases[0]);
          }
        }
      }
    } catch {
      // fall back to text input
    } finally {
      setLoadingDatabases(false);
    }
  }, [adapter, provider, formData, updateField]);

  const hasErrors = Object.values(errors).some(Boolean);

  const renderDatabaseField = () => {
    if (provider === 'sqlite') return null;
    const dbValue = (formData as { database?: string }).database || '';

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

      {!useRawString && adapter.renderFields({
        data: formData,
        isDisabled,
        updateField,
        renderField,
        renderSelect,
        renderDatabaseField,
      })}

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
                    <div className="conn-form__alert conn-form__alert--success" role="status">
                      WordPress credentials will be discovered server-side when the saved vault entry connects.
                    </div>
                  )}
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

      {provider === 'sqlite' && (
        <>
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
                placeholder="Data Source=/path/to/database.db"
              />
            </div>
          )}
        </>
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
