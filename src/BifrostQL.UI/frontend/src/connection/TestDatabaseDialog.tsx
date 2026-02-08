import React, { useState, useCallback, useEffect } from 'react';
import { TestDatabaseTemplate, TestDatabaseProgress } from './types';

const styles = {
  overlay: {
    position: 'fixed' as const,
    top: 0,
    left: 0,
    right: 0,
    bottom: 0,
    backgroundColor: 'rgba(0, 0, 0, 0.75)',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    padding: '1rem',
    zIndex: 1000,
    backdropFilter: 'blur(4px)',
  } as React.CSSProperties,
  dialog: {
    backgroundColor: 'var(--color-bg-primary, #0f172a)',
    borderRadius: '1rem',
    maxWidth: '600px',
    width: '100%',
    maxHeight: '90vh',
    overflow: 'auto',
    border: '1px solid var(--color-border, #334155)',
    boxShadow: '0 20px 60px rgba(0, 0, 0, 0.5)',
  } as React.CSSProperties,
  header: {
    padding: '1.5rem',
    borderBottom: '1px solid var(--color-border, #334155)',
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
  } as React.CSSProperties,
  title: {
    fontSize: '1.25rem',
    fontWeight: '600',
    margin: 0,
    color: 'var(--color-text-primary, #e2e8f0)',
  } as React.CSSProperties,
  closeButton: {
    background: 'none',
    border: 'none',
    fontSize: '1.5rem',
    color: 'var(--color-text-secondary, #94a3b8)',
    cursor: 'pointer',
    padding: '0.25rem',
    lineHeight: 1,
    borderRadius: '0.25rem',
    transition: 'all 0.2s',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: '2rem',
    height: '2rem',
  } as React.CSSProperties,
  closeButtonHover: {
    color: 'var(--color-text-primary, #e2e8f0)',
    backgroundColor: 'var(--color-bg-secondary, #1e293b)',
  } as React.CSSProperties,
  body: {
    padding: '1.5rem',
  } as React.CSSProperties,
  description: {
    fontSize: '0.875rem',
    color: 'var(--color-text-secondary, #94a3b8)',
    marginBottom: '1.5rem',
    lineHeight: '1.6',
  } as React.CSSProperties,
  templateList: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: '0.75rem',
  } as React.CSSProperties,
  templateCard: {
    display: 'flex',
    alignItems: 'flex-start',
    gap: '1rem',
    padding: '1rem',
    border: '2px solid var(--color-border, #334155)',
    borderRadius: '0.75rem',
    cursor: 'pointer',
    transition: 'all 0.2s',
    backgroundColor: 'var(--color-bg-secondary, #1e293b)',
    textAlign: 'left' as const,
  } as React.CSSProperties,
  templateCardHover: {
    borderColor: 'var(--color-primary, #3b82f6)',
    backgroundColor: 'rgba(59, 130, 246, 0.05)',
  } as React.CSSProperties,
  templateCardSelected: {
    borderColor: 'var(--color-primary, #3b82f6)',
    backgroundColor: 'rgba(59, 130, 246, 0.1)',
  } as React.CSSProperties,
  templateCardDisabled: {
    opacity: 0.5,
    cursor: 'not-allowed',
  } as React.CSSProperties,
  templateRadio: {
    marginTop: '0.25rem',
    minWidth: '1.125rem',
    minHeight: '1.125rem',
    accentColor: 'var(--color-primary, #3b82f6)',
  } as React.CSSProperties,
  templateContent: {
    flex: 1,
  } as React.CSSProperties,
  templateTitle: {
    fontSize: '1rem',
    fontWeight: '600',
    margin: 0,
    marginBottom: '0.25rem',
    color: 'var(--color-text-primary, #e2e8f0)',
  } as React.CSSProperties,
  templateDescription: {
    fontSize: '0.875rem',
    margin: 0,
    color: 'var(--color-text-secondary, #94a3b8)',
    lineHeight: '1.5',
  } as React.CSSProperties,
  templateFeatures: {
    display: 'flex',
    flexWrap: 'wrap' as const,
    gap: '0.5rem',
    marginTop: '0.5rem',
  } as React.CSSProperties,
  templateFeature: {
    fontSize: '0.75rem',
    padding: '0.125rem 0.5rem',
    backgroundColor: 'var(--color-bg-tertiary, #334155)',
    borderRadius: '0.25rem',
    color: 'var(--color-text-secondary, #94a3b8)',
  } as React.CSSProperties,
  footer: {
    padding: '1rem 1.5rem',
    borderTop: '1px solid var(--color-border, #334155)',
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
  } as React.CSSProperties,
  error: {
    fontSize: '0.875rem',
    color: 'var(--color-danger, #ef4444)',
    padding: '0.75rem',
    backgroundColor: 'rgba(239, 68, 68, 0.1)',
    borderRadius: '0.5rem',
    border: '1px solid rgba(239, 68, 68, 0.2)',
  } as React.CSSProperties,
  button: {
    padding: '0.75rem 1.5rem',
    fontSize: '0.875rem',
    fontWeight: '500',
    border: 'none',
    borderRadius: '0.5rem',
    cursor: 'pointer',
    transition: 'all 0.2s',
  } as React.CSSProperties,
  buttonSecondary: {
    backgroundColor: 'var(--color-bg-tertiary, #334155)',
    color: 'var(--color-text-primary, #e2e8f0)',
    border: '1px solid var(--color-border, #475569)',
  } as React.CSSProperties,
  buttonSecondaryHover: {
    backgroundColor: 'var(--color-bg-secondary, #475569)',
  } as React.CSSProperties,
  buttonPrimary: {
    backgroundColor: 'var(--color-primary, #3b82f6)',
    color: 'white',
  } as React.CSSProperties,
  buttonPrimaryHover: {
    backgroundColor: 'var(--color-primary-hover, #2563eb)',
  } as React.CSSProperties,
  buttonDisabled: {
    opacity: 0.5,
    cursor: 'not-allowed',
  } as React.CSSProperties,
  progressContainer: {
    padding: '1.5rem',
  } as React.CSSProperties,
  progressHeader: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    marginBottom: '1rem',
  } as React.CSSProperties,
  progressTitle: {
    fontSize: '1rem',
    fontWeight: '600',
    margin: 0,
    color: 'var(--color-text-primary, #e2e8f0)',
  } as React.CSSProperties,
  progressPercent: {
    fontSize: '0.875rem',
    color: 'var(--color-text-secondary, #94a3b8)',
  } as React.CSSProperties,
  progressBar: {
    height: '0.5rem',
    backgroundColor: 'var(--color-bg-tertiary, #334155)',
    borderRadius: '0.25rem',
    overflow: 'hidden',
    marginBottom: '0.75rem',
  } as React.CSSProperties,
  progressFill: {
    height: '100%',
    backgroundColor: 'var(--color-primary, #3b82f6)',
    transition: 'width 0.3s ease',
    borderRadius: '0.25rem',
  } as React.CSSProperties,
  progressMessage: {
    fontSize: '0.875rem',
    color: 'var(--color-text-secondary, #94a3b8)',
    margin: 0,
  } as React.CSSProperties,
  successContainer: {
    padding: '2rem',
    textAlign: 'center' as const,
  } as React.CSSProperties,
  successIcon: {
    fontSize: '4rem',
    marginBottom: '1rem',
  } as React.CSSProperties,
  successTitle: {
    fontSize: '1.25rem',
    fontWeight: '600',
    margin: 0,
    marginBottom: '0.5rem',
    color: 'var(--color-text-primary, #e2e8f0)',
  } as React.CSSProperties,
  successMessage: {
    fontSize: '0.875rem',
    color: 'var(--color-text-secondary, #94a3b8)',
    margin: 0,
    marginBottom: '1.5rem',
  } as React.CSSProperties,
  spinner: {
    width: '1rem',
    height: '1rem',
    border: '2px solid transparent',
    borderTopColor: 'currentColor',
    borderRadius: '50%',
    animation: 'spin 0.8s linear infinite',
    marginRight: '0.5rem',
  } as React.CSSProperties,
};

const injectKeyframes = () => {
  if (typeof document === 'undefined') return;
  const styleId = 'test-db-dialog-styles';
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

interface TemplateInfo {
  id: TestDatabaseTemplate;
  title: string;
  description: string;
  features: string[];
}

const TEMPLATES: TemplateInfo[] = [
  {
    id: TestDatabaseTemplate.Northwind,
    title: 'Northwind',
    description: 'Classic sample database with customers, orders, products, and suppliers.',
    features: ['8 tables', 'relationships', 'queries', 'stored procedures'],
  },
  {
    id: TestDatabaseTemplate.AdventureWorksLite,
    title: 'AdventureWorks Lite',
    description: 'Simplified version of the comprehensive AdventureWorks sample database.',
    features: ['sales tables', 'products', 'customers', 'employees'],
  },
  {
    id: TestDatabaseTemplate.SimpleBlog,
    title: 'Simple Blog',
    description: 'Minimal blog schema with posts, comments, users, and tags.',
    features: ['5 tables', 'many-to-many', 'soft-delete', 'audit fields'],
  },
];

interface TestDatabaseDialogProps {
  isOpen: boolean;
  onClose: () => void;
  onCreate: (template: TestDatabaseTemplate) => Promise<void>;
  onConnectAfterCreate?: (connectionString: string) => void;
  isCreating?: boolean;
  progress?: TestDatabaseProgress | null;
  error?: string | null;
}

const STAGES = {
  selecting: 'selecting',
  creating: 'creating',
  success: 'success',
};

export const TestDatabaseDialog: React.FC<TestDatabaseDialogProps> = ({
  isOpen,
  onClose,
  onCreate,
  onConnectAfterCreate,
  isCreating = false,
  progress = null,
  error = null,
}) => {
  injectKeyframes();

  const [selectedTemplate, setSelectedTemplate] = useState<TestDatabaseTemplate | null>(null);
  const [stage, setStage] = useState(STAGES.selecting);
  const [hoveredTemplate, setHoveredTemplate] = useState<TestDatabaseTemplate | null>(null);
  const [isCloseHovered, setIsCloseHovered] = useState(false);

  useEffect(() => {
    if (!isOpen) {
      setStage(STAGES.selecting);
      setSelectedTemplate(null);
    }
  }, [isOpen]);

  useEffect(() => {
    if (isCreating && stage === STAGES.selecting) {
      setStage(STAGES.creating);
    }
  }, [isCreating, stage]);

  useEffect(() => {
    if (progress?.percent === 100 && stage === STAGES.creating) {
      setStage(STAGES.success);
    }
  }, [progress, stage]);

  const handleCreate = useCallback(async () => {
    if (selectedTemplate === null) return;

    try {
      await onCreate(selectedTemplate);
    } catch (err) {
      console.error('Failed to create test database:', err);
    }
  }, [selectedTemplate, onCreate]);

  const handleConnect = useCallback(() => {
    if (onConnectAfterCreate && progress?.connectionString) {
      onConnectAfterCreate(progress.connectionString);
      onClose();
    }
  }, [onConnectAfterCreate, progress, onClose]);

  const handleTemplateSelect = useCallback((template: TestDatabaseTemplate) => {
    if (!isCreating) {
      setSelectedTemplate(template);
    }
  }, [isCreating]);

  const handleKeyDown = useCallback((e: React.KeyboardEvent) => {
    if (e.key === 'Escape' && !isCreating) {
      onClose();
    }
  }, [isCreating, onClose]);

  if (!isOpen) return null;

  const renderTemplateSelection = () => (
    <>
      <div style={styles.header}>
        <h2 style={styles.title}>Create Test Database</h2>
        <button
          type="button"
          onClick={onClose}
          disabled={isCreating}
          style={{
            ...styles.closeButton,
            ...(isCloseHovered && !isCreating ? styles.closeButtonHover : {}),
          }}
          onMouseEnter={() => setIsCloseHovered(true)}
          onMouseLeave={() => setIsCloseHovered(false)}
          aria-label="Close dialog"
        >
          ×
        </button>
      </div>

      <div style={styles.body}>
        <p style={styles.description}>
          Choose a template to create a sample database. This will create a new database
          on your local SQL Server instance with sample data for exploring BifrostQL.
        </p>

        <div style={styles.templateList} role="radiogroup" aria-label="Database templates">
          {TEMPLATES.map((template) => (
            <div
              key={template.id}
              role="radio"
              aria-checked={selectedTemplate === template.id}
              tabIndex={isCreating ? -1 : 0}
              onKeyDown={(e) => {
                if (e.key === 'Enter' || e.key === ' ') {
                  e.preventDefault();
                  handleTemplateSelect(template.id);
                }
              }}
              onClick={() => handleTemplateSelect(template.id)}
              onMouseEnter={() => setHoveredTemplate(template.id)}
              onMouseLeave={() => setHoveredTemplate(null)}
              style={{
                ...styles.templateCard,
                ...(selectedTemplate === template.id ? styles.templateCardSelected : {}),
                ...(hoveredTemplate === template.id && !isCreating ? styles.templateCardHover : {}),
                ...(isCreating ? styles.templateCardDisabled : {}),
              }}
            >
              <input
                type="radio"
                name="template"
                checked={selectedTemplate === template.id}
                onChange={() => handleTemplateSelect(template.id)}
                disabled={isCreating}
                style={styles.templateRadio}
                aria-label={`Select ${template.title} template`}
              />
              <div style={styles.templateContent}>
                <h3 style={styles.templateTitle}>{template.title}</h3>
                <p style={styles.templateDescription}>{template.description}</p>
                <div style={styles.templateFeatures}>
                  {template.features.map((feature) => (
                    <span key={feature} style={styles.templateFeature}>
                      {feature}
                    </span>
                  ))}
                </div>
              </div>
            </div>
          ))}
        </div>
      </div>

      <div style={styles.footer}>
        {error && <div style={styles.error} role="alert">{error}</div>}
        <div style={{ flex: 1 }} />
        <button
          type="button"
          onClick={onClose}
          disabled={isCreating}
          style={{
            ...styles.button,
            ...styles.buttonSecondary,
            marginRight: '0.75rem',
            ...(isCreating ? styles.buttonDisabled : {}),
          }}
        >
          Cancel
        </button>
        <button
          type="button"
          onClick={handleCreate}
          disabled={selectedTemplate === null || isCreating}
          style={{
            ...styles.button,
            ...styles.buttonPrimary,
            ...(selectedTemplate === null || isCreating ? styles.buttonDisabled : {}),
          }}
        >
          {isCreating && <span style={styles.spinner} aria-hidden="true" />}
          Create Database
        </button>
      </div>
    </>
  );

  const renderProgress = () => (
    <>
      <div style={styles.header}>
        <h2 style={styles.title}>Creating Database...</h2>
        <button
          type="button"
          onClick={onClose}
          disabled={isCreating}
          style={{
            ...styles.closeButton,
            ...(isCloseHovered && !isCreating ? styles.closeButtonHover : {}),
          }}
          onMouseEnter={() => setIsCloseHovered(true)}
          onMouseLeave={() => setIsCloseHovered(false)}
          aria-label="Close dialog"
        >
          ×
        </button>
      </div>

      <div style={styles.progressContainer}>
        <div style={styles.progressHeader}>
          <h3 style={styles.progressTitle}>
            {progress?.stage || 'Initializing...'}
          </h3>
          <span style={styles.progressPercent}>
            {progress?.percent ?? 0}%
          </span>
        </div>

        <div style={styles.progressBar} role="progressbar" aria-valuenow={progress?.percent ?? 0} aria-valuemin={0} aria-valuemax={100}>
          <div
            style={{
              ...styles.progressFill,
              width: `${progress?.percent ?? 0}%`,
            }}
          />
        </div>

        <p style={styles.progressMessage}>
          {progress?.message || 'Please wait while we create your test database...'}
        </p>
      </div>
    </>
  );

  const renderSuccess = () => (
    <>
      <div style={styles.header}>
        <h2 style={styles.title}>Database Created!</h2>
        <button
          type="button"
          onClick={onClose}
          style={{
            ...styles.closeButton,
            ...(isCloseHovered ? styles.closeButtonHover : {}),
          }}
          onMouseEnter={() => setIsCloseHovered(true)}
          onMouseLeave={() => setIsCloseHovered(false)}
          aria-label="Close dialog"
        >
          ×
        </button>
      </div>

      <div style={styles.successContainer}>
        <div style={styles.successIcon}>🎉</div>
        <h3 style={styles.successTitle}>Test Database Ready</h3>
        <p style={styles.successMessage}>
          Your {TEMPLATES.find(t => t.id === selectedTemplate)?.title || 'test'} database
          has been created successfully. You can now start exploring BifrostQL!
        </p>

        {onConnectAfterCreate && (
          <button
            type="button"
            onClick={handleConnect}
            style={{
              ...styles.button,
              ...styles.buttonPrimary,
              padding: '0.875rem 2rem',
              fontSize: '1rem',
            }}
          >
            Connect and Explore
          </button>
        )}
      </div>
    </>
  );

  return (
    <div
      style={styles.overlay}
      onClick={isCreating ? undefined : onClose}
      onKeyDown={handleKeyDown}
      role="dialog"
      aria-modal="true"
      aria-labelledby="test-db-dialog-title"
    >
      <div
        style={styles.dialog}
        onClick={(e) => e.stopPropagation()}
      >
        {stage === STAGES.selecting && renderTemplateSelection()}
        {stage === STAGES.creating && renderProgress()}
        {stage === STAGES.success && renderSuccess()}
      </div>
    </div>
  );
};

export default TestDatabaseDialog;
