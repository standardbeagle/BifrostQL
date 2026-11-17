import type { CSSProperties, KeyboardEvent, ReactElement } from 'react';
import type {
  ColumnConfig,
  EditorType,
} from '../hooks/use-bifrost-table.types';

/**
 * Pick the editor for a column: the column's `editorType` when set, else the
 * runtime type of the row's stored value. A number edits as a number and a
 * boolean as a checkbox, so the value written back keeps the column's type
 * instead of becoming the display string ("Yes", "42", a locale date).
 */
export function resolveEditorType(
  column: ColumnConfig,
  originalValue: unknown,
): EditorType {
  if (column.editorType) return column.editorType;
  if (typeof originalValue === 'boolean') return 'checkbox';
  if (typeof originalValue === 'number') return 'number';
  return 'text';
}

/**
 * Turn an input's text back into the column's value. An empty number field
 * is `null` (cleared), never `0` or `""`. A select maps back to the option's
 * own value so a numeric option stays a number.
 */
export function parseEditorInput(
  editorType: EditorType,
  text: string,
  column: ColumnConfig,
): unknown {
  switch (editorType) {
    case 'number':
      return text.trim() === '' ? null : Number(text);
    case 'select': {
      const option = column.editorOptions?.find(
        (o) => String(o.value) === text,
      );
      return option ? option.value : text;
    }
    default:
      return text;
  }
}

/** The raw value as input text — no display formatting. */
function toInputText(value: unknown): string {
  return value === null || value === undefined ? '' : String(value);
}

const DATE_ONLY = /^\d{4}-\d{2}-\d{2}$/;

const inputStyle: CSSProperties = {
  width: '100%',
  padding: '2px 4px',
  border: '1px solid #3b82f6',
  borderRadius: '2px',
  outline: 'none',
  fontSize: 'inherit',
};

export interface InlineCellEditorProps {
  column: ColumnConfig;
  /** The current (possibly edited) cell value. */
  value: unknown;
  /** The row's stored value for this field, used to pick the editor. */
  originalValue: unknown;
  row: Record<string, unknown>;
  onChange: (value: unknown) => void;
  onCommit: () => void;
  onCancel: () => void;
}

/**
 * The inline editor for one cell. A column's `editorComponent` wins;
 * otherwise the editor follows {@link resolveEditorType} and edits the raw
 * value, so what reaches `onRowUpdate` has the column's type.
 */
export function InlineCellEditor({
  column,
  value,
  originalValue,
  row,
  onChange,
  onCommit,
  onCancel,
}: InlineCellEditorProps) {
  if (column.editorComponent) {
    return column.editorComponent({
      value,
      onChange,
      onCommit,
      onCancel,
      column,
      row,
    }) as ReactElement;
  }

  const editorType = resolveEditorType(column, originalValue);
  const onKeyDown = (e: KeyboardEvent) => {
    if (e.key === 'Enter' && editorType !== 'textarea') {
      e.preventDefault();
      onCommit();
    } else if (e.key === 'Escape') {
      onCancel();
    }
  };
  // Blur and Enter both commit: clicking away from a cell the user has typed
  // into must not discard the edit.
  const common = {
    onBlur: onCommit,
    onKeyDown,
    autoFocus: true,
    'data-testid': 'edit-input',
    'aria-label': column.header,
    style: inputStyle,
  };

  if (editorType === 'checkbox') {
    return (
      <input
        type="checkbox"
        checked={value === true}
        onChange={(e) => onChange(e.target.checked)}
        {...common}
        style={undefined}
      />
    );
  }

  if (editorType === 'select') {
    return (
      <select
        value={toInputText(value)}
        onChange={(e) =>
          onChange(parseEditorInput('select', e.target.value, column))
        }
        {...common}
      >
        {(column.editorOptions ?? []).map((o) => (
          <option key={String(o.value)} value={String(o.value)}>
            {o.label}
          </option>
        ))}
      </select>
    );
  }

  if (editorType === 'textarea') {
    return (
      <textarea
        value={toInputText(value)}
        onChange={(e) => onChange(e.target.value)}
        {...common}
      />
    );
  }

  const text = toInputText(value);
  // A date picker only for a date-only value: a timestamp in a date picker
  // would drop its time, so it is edited as its raw ISO text instead.
  const inputType =
    editorType === 'number'
      ? 'number'
      : editorType === 'date' && (text === '' || DATE_ONLY.test(text))
        ? 'date'
        : 'text';
  return (
    <input
      type={inputType}
      value={text}
      onChange={(e) =>
        onChange(parseEditorInput(editorType, e.target.value, column))
      }
      {...common}
    />
  );
}
