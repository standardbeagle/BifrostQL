import React, { useState, useCallback, useEffect } from 'react';
import {
  ConnectionFormData,
  AuthMethod,
  ConnectionFormErrors,
  ConnectionState
} from './types';

interface ConnectionFormProps {
  onConnect: (connectionString: string, connectionName: string) => void;
  onTestConnection?: (connectionString: string) => Promise<boolean>;
  initialState?: ConnectionState;
}

const styles = {
  container: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: '1.5rem',
    padding: '2rem',
    maxWidth: '500px',
    width: '100%',
    margin: '0 auto',
  } as React.CSSProperties,
  title: {
    fontSize: '1.5rem',
    fontWeight: '600',
    margin: 0,
    marginBottom: '0.5rem',
    color: 'var(--color-text-primary, #e2e8f0)',
  } as React.CSSProperties,
  subtitle: {
    fontSize: '0.875rem',
    color: 'var(--color-text-secondary, #94a3b8)',
    margin: 0,
  } as React.CSSProperties,
  formGroup: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: '0.5rem',
  } as React.CSSProperties,
  label: {
    fontSize: '0.875rem',
    fontWeight: '500',
    color: 'var(--color-text-primary, #e2e8f0)',
    display: 'flex',
    alignItems: 'center',
    gap: '0.25rem',
  } as React.CSSProperties,
  required: {
    color: 'var(--color-danger, #ef4444)',
  } as React.CSSProperties,
  input: {
    padding: '0.75rem 1rem',
    fontSize: '0.875rem',
    border: '1px solid var(--color-border, #334155)',
    borderRadius: '0.5rem',
    backgroundColor: 'var(--color-bg-secondary, #1e293b)',
    color: 'var(--color-text-primary, #e2e8f0)',
    transition: 'border-color 0.2s, box-shadow 0.2s',
    outline: 'none',
  } as React.CSSProperties,
  inputError: {
    borderColor: 'var(--color-danger, #ef4444)',
  } as React.CSSProperties,
  inputFocus: {
    borderColor: 'var(--color-primary, #3b82f6)',
    boxShadow: '0 0 0 3px rgba(59, 130, 246, 0.1)',
  } as React.CSSProperties,
  errorText: {
    fontSize: '0.75rem',
    color: 'var(--color-danger, #ef4444)',
    marginTop: '0.25rem',
  } as React.CSSProperties,
  radioGroup: {
    display: 'flex',
    gap: '1rem',
    padding: '0.75rem',
    border: '1px solid var(--color-border, #334155)',
    borderRadius: '0.5rem',
    backgroundColor: 'var(--color-bg-secondary, #1e293b)',
  } as React.CSSProperties,
  radioLabel: {
    display: 'flex',
    alignItems: 'center',
    gap: '0.5rem',
    fontSize: '0.875rem',
    color: 'var(--color-text-primary, #e2e8f0)',
    cursor: 'pointer',
  } as React.CSSProperties,
  checkbox: {
    display: 'flex',
    alignItems: 'center',
    gap: '0.75rem',
    padding: '0.75rem',
    border: '1px solid var(--color-border, #334155)',
    borderRadius: '0.5rem',
    backgroundColor: 'var(--color-bg-secondary, #1e293b)',
    cursor: 'pointer',
  } as React.CSSProperties,
  checkboxLabel: {
    fontSize: '0.875rem',
    color: 'var(--color-text-primary, #e2e8f0)',
    cursor: 'pointer',
    userSelect: 'none' as const,
  } as React.CSSProperties,
  buttonGroup: {
    display: 'flex',
    gap: '1rem',
    marginTop: '1rem',
  } as React.CSSProperties,
  button: {
    flex: 1,
    padding: '0.875rem 1.5rem',
    fontSize: '0.875rem',
    fontWeight: '500',
    border: 'none',
    borderRadius: '0.5rem',
    cursor: 'pointer',
    transition: 'all 0.2s',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '0.5rem',
  } as React.CSSProperties,
  buttonPrimary: {
    backgroundColor: 'var(--color-primary, #3b82f6)',
    color: 'white',
  } as React.CSSProperties,
  buttonPrimaryHover: {
    backgroundColor: 'var(--color-primary-hover, #2563eb)',
  } as React.CSSProperties,
  buttonSecondary: {
    backgroundColor: 'var(--color-bg-tertiary, #334155)',
    color: 'var(--color-text-primary, #e2e8f0)',
    border: '1px solid var(--color-border, #475569)',
  } as React.CSSProperties,
  buttonSecondaryHover: {
    backgroundColor: 'var(--color-bg-secondary, #475569)',
  } as React.CSSProperties,
  buttonDisabled: {
    opacity: 0.5,
    cursor: 'not-allowed',
  } as React.CSSProperties,
  alert: {
    padding: '0.75rem 1rem',
    borderRadius: '0.5rem',
    fontSize: '0.875rem',
    display: 'flex',
    alignItems: 'center',
    gap: '0.5rem',
  } as React.CSSProperties,
  alertError: {
    backgroundColor: 'rgba(239, 68, 68, 0.1)',
    color: 'var(--color-danger, #ef4444)',
    border: '1px solid rgba(239, 68, 68, 0.2)',
  } as React.CSSProperties,
  alertSuccess: {
    backgroundColor: 'rgba(34, 197, 94, 0.1)',
    color: 'var(--color-success, #22c55e)',
    border: '1px solid rgba(34, 197, 94, 0.2)',
  } as React.CSSProperties,
  spinner: {
    width: '1rem',
    height: '1rem',
    border: '2px solid transparent',
    borderTopColor: 'currentColor',
    borderRadius: '50%',
    animation: 'spin 0.8s linear infinite',
  } as React.CSSProperties,
};

const injectKeyframes = () => {
  if (typeof document === 'undefined') return;
  const styleId = 'connection-form-styles';
  if (!document.getElementById(styleId)) {
    const style = document.createElement('style');
    style.id = styleId;
    style.textContent = `
      @keyframes spin {
        to { transform: rotate(360deg); }
      }
    `;
    document.head.appendChild(style);
  }
};

const validateServer = (server: string): string | undefined => {
  if (!server.trim()) {
    return 'Server address is required';
  }
  if (server.trim().length > 255) {
    return 'Server address is too long';
  }
  return undefined;
};

const validateDatabase = (database: string): string | undefined => {
  if (!database.trim()) {
    return 'Database name is required';
  }
  if (!/^[a-zA-Z_][a-zA-Z0-9_]*$/.test(database.trim())) {
    return 'Database name must start with a letter or underscore and contain only letters, numbers, and underscores';
  }
  if (database.trim().length > 128) {
    return 'Database name is too long (max 128 characters)';
  }
  return undefined;
};

const validateUsername = (username: string | undefined, authMethod: AuthMethod): string | undefined => {
  if (authMethod === AuthMethod.SqlServer) {
    if (!username?.trim()) {
      return 'Username is required for SQL Server Authentication';
    }
    if (username.trim().length > 128) {
      return 'Username is too long';
    }
  }
  return undefined;
};

const validatePassword = (password: string | undefined, authMethod: AuthMethod): string | undefined => {
  if (authMethod === AuthMethod.SqlServer) {
    if (!password) {
      return 'Password is required for SQL Server Authentication';
    }
  }
  return undefined;
};

const buildConnectionString = (data: ConnectionFormData): string => {
  const parts: string[] = [
    `Server=${data.server}`,
    `Database=${data.database}`,
  ];

  if (data.authMethod === AuthMethod.SqlServer) {
    parts.push(`User Id=${data.username}`);
    parts.push(`Password=${data.password}`);
  } else {
    parts.push('Trusted_Connection=Yes');
  }

  if (data.trustServerCertificate) {
    parts.push('TrustServerCertificate=True');
  }

  return parts.join(';');
};

export const ConnectionForm: React.FC<ConnectionFormProps> = ({
  onConnect,
  onTestConnection,
  initialState = 'idle'
}) => {
  injectKeyframes();

  const [formData, setFormData] = useState<ConnectionFormData>({
    server: 'localhost',
    database: '',
    authMethod: AuthMethod.SqlServer,
    username: '',
    password: '',
    trustServerCertificate: true,
  });

  const [errors, setErrors] = useState<ConnectionFormErrors>({});
  const [touched, setTouched] = useState<Set<string>>(new Set());
  const [connectionState, setConnectionState] = useState<ConnectionState>(initialState);
  const [testResult, setTestResult] = useState<{ success: boolean; message: string } | null>(null);

  const validateField = useCallback((field: keyof ConnectionFormData, value: any): string | undefined => {
    switch (field) {
      case 'server':
        return validateServer(value);
      case 'database':
        return validateDatabase(value);
      case 'username':
        return validateUsername(value, formData.authMethod);
      case 'password':
        return validatePassword(value, formData.authMethod);
      default:
        return undefined;
    }
  }, [formData.authMethod]);

  const validateForm = useCallback((): boolean => {
    const newErrors: ConnectionFormErrors = {
      server: validateField('server', formData.server),
      database: validateField('database', formData.database),
      username: validateField('username', formData.username),
      password: validateField('password', formData.password),
    };

    setErrors(newErrors);
    return !Object.values(newErrors).some(error => error !== undefined);
  }, [formData, validateField]);

  const handleFieldChange = useCallback(<K extends keyof ConnectionFormData>(
    field: K,
    value: ConnectionFormData[K]
  ) => {
    setFormData(prev => ({ ...prev, [field]: value }));
    setTestResult(null);

    if (touched.has(field)) {
      const error = validateField(field, value);
      setErrors(prev => ({ ...prev, [field]: error }));
    }
  }, [touched, validateField]);

  const handleFieldBlur = useCallback((field: keyof ConnectionFormData) => {
    setTouched(prev => new Set(prev).add(field));
    const error = validateField(field, formData[field]);
    setErrors(prev => ({ ...prev, [field]: error }));
  }, [formData, validateField]);

  const handleTestConnection = useCallback(async () => {
    if (!validateForm()) {
      return;
    }

    if (!onTestConnection) {
      return;
    }

    setConnectionState('testing');
    setTestResult(null);

    try {
      const connectionString = buildConnectionString(formData);
      const success = await onTestConnection(connectionString);
      setTestResult({
        success,
        message: success
          ? 'Connection successful! You can now connect.'
          : 'Connection failed. Please check your credentials and try again.'
      });
    } catch (error) {
      setTestResult({
        success: false,
        message: error instanceof Error ? error.message : 'An unexpected error occurred'
      });
    } finally {
      setConnectionState('idle');
    }
  }, [formData, validateForm, onTestConnection]);

  const handleConnect = useCallback(() => {
    if (!validateForm()) {
      return;
    }

    setConnectionState('connecting');
    const connectionString = buildConnectionString(formData);
    const connectionName = `${formData.server}/${formData.database}`;

    onConnect(connectionString, connectionName);
  }, [formData, validateForm, onConnect]);

  const isConnectingOrTesting = connectionState === 'connecting' || connectionState === 'testing';
  const hasErrors = Object.values(errors).some(error => error !== undefined);

  const getInputStyle = (field: keyof ConnectionFormErrors): React.CSSProperties => {
    const baseStyle = { ...styles.input };
    if (errors[field]) {
      return { ...baseStyle, ...styles.inputError };
    }
    return baseStyle;
  };

  useEffect(() => {
    if (formData.authMethod === AuthMethod.Windows) {
      setErrors(prev => ({ ...prev, username: undefined, password: undefined }));
    }
  }, [formData.authMethod]);

  return (
    <div style={styles.container} role="form" aria-label="Database connection form">
      <div>
        <h1 style={styles.title}>Connect to Database</h1>
        <p style={styles.subtitle}>Enter your SQL Server connection details below</p>
      </div>

      {testResult && (
        <div
          style={{ ...styles.alert, ...(testResult.success ? styles.alertSuccess : styles.alertError) }}
          role="alert"
          aria-live="polite"
        >
          {testResult.success ? '✓' : '⚠'} {testResult.message}
        </div>
      )}

      <div style={styles.formGroup}>
        <label htmlFor="server" style={styles.label}>
          Server Address <span style={styles.required} aria-label="required">*</span>
        </label>
        <input
          id="server"
          type="text"
          value={formData.server}
          onChange={(e) => handleFieldChange('server', e.target.value)}
          onBlur={() => handleFieldBlur('server')}
          disabled={isConnectingOrTesting}
          placeholder="localhost"
          aria-required="true"
          aria-invalid={!!errors.server}
          aria-describedby={errors.server ? 'server-error' : undefined}
          style={getInputStyle('server')}
        />
        {errors.server && (
          <span id="server-error" style={styles.errorText} role="alert">
            {errors.server}
          </span>
        )}
      </div>

      <div style={styles.formGroup}>
        <label htmlFor="database" style={styles.label}>
          Database Name <span style={styles.required} aria-label="required">*</span>
        </label>
        <input
          id="database"
          type="text"
          value={formData.database}
          onChange={(e) => handleFieldChange('database', e.target.value)}
          onBlur={() => handleFieldBlur('database')}
          disabled={isConnectingOrTesting}
          placeholder="my_database"
          aria-required="true"
          aria-invalid={!!errors.database}
          aria-describedby={errors.database ? 'database-error' : undefined}
          style={getInputStyle('database')}
        />
        {errors.database && (
          <span id="database-error" style={styles.errorText} role="alert">
            {errors.database}
          </span>
        )}
      </div>

      <fieldset style={{ border: 'none', padding: 0, margin: 0 }}>
        <legend style={styles.label}>Authentication Method</legend>
        <div style={styles.radioGroup} role="radiogroup" aria-label="Authentication method">
          <label style={styles.radioLabel}>
            <input
              type="radio"
              name="authMethod"
              value={AuthMethod.SqlServer}
              checked={formData.authMethod === AuthMethod.SqlServer}
              onChange={(e) => handleFieldChange('authMethod', e.target.value as AuthMethod)}
              disabled={isConnectingOrTesting}
            />
            SQL Server Authentication
          </label>
          <label style={styles.radioLabel}>
            <input
              type="radio"
              name="authMethod"
              value={AuthMethod.Windows}
              checked={formData.authMethod === AuthMethod.Windows}
              onChange={(e) => handleFieldChange('authMethod', e.target.value as AuthMethod)}
              disabled={isConnectingOrTesting}
            />
            Windows Authentication
          </label>
        </div>
      </fieldset>

      {formData.authMethod === AuthMethod.SqlServer && (
        <>
          <div style={styles.formGroup}>
            <label htmlFor="username" style={styles.label}>
              Username <span style={styles.required} aria-label="required">*</span>
            </label>
            <input
              id="username"
              type="text"
              value={formData.username}
              onChange={(e) => handleFieldChange('username', e.target.value)}
              onBlur={() => handleFieldBlur('username')}
              disabled={isConnectingOrTesting}
              placeholder="sa"
              aria-required="true"
              aria-invalid={!!errors.username}
              aria-describedby={errors.username ? 'username-error' : undefined}
              style={getInputStyle('username')}
            />
            {errors.username && (
              <span id="username-error" style={styles.errorText} role="alert">
                {errors.username}
              </span>
            )}
          </div>

          <div style={styles.formGroup}>
            <label htmlFor="password" style={styles.label}>
              Password <span style={styles.required} aria-label="required">*</span>
            </label>
            <input
              id="password"
              type="password"
              value={formData.password}
              onChange={(e) => handleFieldChange('password', e.target.value)}
              onBlur={() => handleFieldBlur('password')}
              disabled={isConnectingOrTesting}
              placeholder="••••••••"
              aria-required="true"
              aria-invalid={!!errors.password}
              aria-describedby={errors.password ? 'password-error' : undefined}
              style={getInputStyle('password')}
            />
            {errors.password && (
              <span id="password-error" style={styles.errorText} role="alert">
                {errors.password}
              </span>
            )}
          </div>
        </>
      )}

      <label style={styles.checkbox}>
        <input
          type="checkbox"
          checked={formData.trustServerCertificate}
          onChange={(e) => handleFieldChange('trustServerCertificate', e.target.checked)}
          disabled={isConnectingOrTesting}
        />
        <span style={styles.checkboxLabel}>Trust Server Certificate</span>
      </label>

      <div style={styles.buttonGroup}>
        <button
          type="button"
          onClick={handleTestConnection}
          disabled={isConnectingOrTesting || hasErrors || !onTestConnection}
          style={{
            ...styles.button,
            ...styles.buttonSecondary,
            ...(isConnectingOrTesting ? styles.buttonDisabled : {}),
          }}
          aria-busy={connectionState === 'testing'}
        >
          {connectionState === 'testing' && <span style={styles.spinner} aria-hidden="true" />}
          Test Connection
        </button>
        <button
          type="button"
          onClick={handleConnect}
          disabled={isConnectingOrTesting || hasErrors}
          style={{
            ...styles.button,
            ...styles.buttonPrimary,
            ...(isConnectingOrTesting ? styles.buttonDisabled : {}),
          }}
          aria-busy={connectionState === 'connecting'}
        >
          {connectionState === 'connecting' && <span style={styles.spinner} aria-hidden="true" />}
          Connect
        </button>
      </div>
    </div>
  );
};

export default ConnectionForm;
