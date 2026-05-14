import type { ChangeEvent } from 'react';

/**
 * Reusable field-control set for metadata-driven CRUD screens.
 *
 * Each control is a controlled component over a single field value. The
 * dispatcher in `field-control.tsx` selects which control to render from a
 * field's {@link import('../metadata/types').FieldMetadata}. Controls are kept
 * deliberately presentation-light (no design-system dependency) so consuming
 * apps can restyle them; the heavier admin UI lives in `examples/edit-db`,
 * which remains the reference/admin tool.
 *
 * Lessons ported from `examples/edit-db`: JSON detection + format/minify from
 * `content-editor.tsx`, and the FK-lookup shape from `fk-cell-popover.tsx`.
 */

/** Common props shared by every field control. */
export interface FieldControlProps {
  /** Field name; used for `id`/`name` and label association. */
  name: string;
  /** Human-readable label. */
  label: string;
  /** Current field value. */
  value: unknown;
  /** Change handler receiving the next value. */
  onChange: (value: unknown) => void;
  /** Whether the control is read-only. */
  readOnly?: boolean;
  /** Help text rendered below the control. */
  helpText?: string;
}

/** Props for {@link EnumSelectControl}: adds the selectable options. */
export interface EnumSelectControlProps extends FieldControlProps {
  /** Allowed values; rendered as `<option>`s. */
  options: string[];
}

/** Props for {@link FkLookupControl}: adds the related-entity descriptor. */
export interface FkLookupControlProps extends FieldControlProps {
  /** Qualified table name of the FK target (e.g. `dbo.users`). */
  targetEntity: string;
  /** Candidate rows for the lookup, each carrying a key and a display label. */
  options: Array<{ key: string; label: string }>;
}

/** Wrap a control with its label and optional help text. */
function FieldShell({
  name,
  label,
  helpText,
  children,
}: {
  name: string;
  label: string;
  helpText?: string;
  children: React.ReactNode;
}) {
  return (
    <div className="bifrost-field" data-testid={`field-${name}`}>
      <label htmlFor={name} className="bifrost-field__label">
        {label}
      </label>
      {children}
      {helpText ? (
        <p className="bifrost-field__help" id={`${name}-help`}>
          {helpText}
        </p>
      ) : null}
    </div>
  );
}

/** Single-line text/number input for scalar fields. */
export function ScalarControl({
  name,
  label,
  value,
  onChange,
  readOnly,
  helpText,
}: FieldControlProps) {
  const handleChange = (e: ChangeEvent<HTMLInputElement>) => {
    onChange(e.target.value);
  };
  return (
    <FieldShell name={name} label={label} helpText={helpText}>
      <input
        id={name}
        name={name}
        type="text"
        className="bifrost-field__input"
        value={value == null ? '' : String(value)}
        readOnly={readOnly}
        aria-describedby={helpText ? `${name}-help` : undefined}
        onChange={handleChange}
      />
    </FieldShell>
  );
}

/** Date input (`type="date"`) for date fields. */
export function DateControl({
  name,
  label,
  value,
  onChange,
  readOnly,
  helpText,
}: FieldControlProps) {
  const handleChange = (e: ChangeEvent<HTMLInputElement>) => {
    onChange(e.target.value);
  };
  return (
    <FieldShell name={name} label={label} helpText={helpText}>
      <input
        id={name}
        name={name}
        type="date"
        className="bifrost-field__input"
        value={value == null ? '' : String(value)}
        readOnly={readOnly}
        aria-describedby={helpText ? `${name}-help` : undefined}
        onChange={handleChange}
      />
    </FieldShell>
  );
}

/** Checkbox for boolean fields. */
export function BooleanControl({
  name,
  label,
  value,
  onChange,
  readOnly,
  helpText,
}: FieldControlProps) {
  const handleChange = (e: ChangeEvent<HTMLInputElement>) => {
    onChange(e.target.checked);
  };
  return (
    <FieldShell name={name} label={label} helpText={helpText}>
      <input
        id={name}
        name={name}
        type="checkbox"
        className="bifrost-field__checkbox"
        checked={value === true}
        disabled={readOnly}
        aria-describedby={helpText ? `${name}-help` : undefined}
        onChange={handleChange}
      />
    </FieldShell>
  );
}

/** `<select>` for enum fields with a fixed option set. */
export function EnumSelectControl({
  name,
  label,
  value,
  onChange,
  options,
  readOnly,
  helpText,
}: EnumSelectControlProps) {
  const handleChange = (e: ChangeEvent<HTMLSelectElement>) => {
    onChange(e.target.value);
  };
  return (
    <FieldShell name={name} label={label} helpText={helpText}>
      <select
        id={name}
        name={name}
        className="bifrost-field__select"
        value={value == null ? '' : String(value)}
        disabled={readOnly}
        aria-describedby={helpText ? `${name}-help` : undefined}
        onChange={handleChange}
      >
        <option value="">{'—'}</option>
        {options.map((opt) => (
          <option key={opt} value={opt}>
            {opt}
          </option>
        ))}
      </select>
    </FieldShell>
  );
}

/**
 * Multi-line textarea for JSON/long-text fields, with a Format action for
 * values that parse as JSON. Ported from `examples/edit-db` `content-editor`.
 */
export function JsonTextControl({
  name,
  label,
  value,
  onChange,
  readOnly,
  helpText,
}: FieldControlProps) {
  const text = value == null ? '' : String(value);

  const handleChange = (e: ChangeEvent<HTMLTextAreaElement>) => {
    onChange(e.target.value);
  };

  const handleFormat = () => {
    try {
      onChange(JSON.stringify(JSON.parse(text), null, 2));
    } catch {
      // Leave the value untouched when it is not valid JSON.
    }
  };

  let isJson = false;
  if (text.trim()) {
    try {
      JSON.parse(text);
      isJson = true;
    } catch {
      isJson = false;
    }
  }

  return (
    <FieldShell name={name} label={label} helpText={helpText}>
      {isJson && !readOnly ? (
        <button
          type="button"
          className="bifrost-field__format"
          data-testid={`field-${name}-format`}
          onClick={handleFormat}
        >
          Format
        </button>
      ) : null}
      <textarea
        id={name}
        name={name}
        className="bifrost-field__textarea"
        value={text}
        readOnly={readOnly}
        rows={6}
        aria-describedby={helpText ? `${name}-help` : undefined}
        onChange={handleChange}
      />
    </FieldShell>
  );
}

/**
 * Foreign-key lookup: a `<select>` over candidate rows of the target entity.
 * The shape mirrors `examples/edit-db` `fk-cell-popover`, but candidate rows
 * are supplied by the caller rather than fetched here, keeping the control
 * router-/fetch-agnostic.
 */
export function FkLookupControl({
  name,
  label,
  value,
  onChange,
  options,
  targetEntity,
  readOnly,
  helpText,
}: FkLookupControlProps) {
  const handleChange = (e: ChangeEvent<HTMLSelectElement>) => {
    onChange(e.target.value);
  };
  return (
    <FieldShell name={name} label={label} helpText={helpText}>
      <select
        id={name}
        name={name}
        className="bifrost-field__fk"
        data-target-entity={targetEntity}
        value={value == null ? '' : String(value)}
        disabled={readOnly}
        aria-describedby={helpText ? `${name}-help` : undefined}
        onChange={handleChange}
      >
        <option value="">{'—'}</option>
        {options.map((opt) => (
          <option key={opt.key} value={opt.key}>
            {opt.label}
          </option>
        ))}
      </select>
    </FieldShell>
  );
}
