/**
 * Maps a saved-form field (plus any app-metadata widget hint) to the concrete
 * input the runner renders, and resolves the field's effective read-only /
 * visible state.
 *
 * The saved definition already carries a `control` chosen in the builder. The
 * app-metadata overlay's {@link FieldWidgetHint} (a structural subset of
 * `@bifrostql/types` `FieldMetadata` — kept local so this shipped stack stays
 * self-contained) can override it at runtime and tighten visibility/read-only,
 * so a server-side presentation change is honoured without re-saving the form.
 */

import type { FormControlType, FormField } from './form-definition';

/** Structural subset of the app-metadata `FieldMetadata` this module reads. */
export interface FieldWidgetHint {
  /** Widget hint (e.g. `text`, `select`) — overrides the saved control when recognised. */
  widget?: string;
  /** Whether the field is visible. Metadata default is `true`. */
  visible?: boolean;
  /** Whether the field is read-only. Metadata default is `false`. */
  readOnly?: boolean;
}

const FORM_CONTROLS = new Set<FormControlType>([
  'text',
  'textarea',
  'number',
  'checkbox',
  'date',
  'datetime',
  'select',
]);

function asControl(value: string | undefined): FormControlType | undefined {
  return value !== undefined && FORM_CONTROLS.has(value as FormControlType)
    ? (value as FormControlType)
    : undefined;
}

/** The rendered control plus its resolved read-only / visible state. */
export interface ResolvedWidget {
  control: FormControlType;
  readOnly: boolean;
  visible: boolean;
}

/**
 * Resolves the effective widget for a field. The metadata widget hint (when it
 * names a known control) wins over the saved control; read-only is the union of
 * both sources (either can lock a field, neither can unlock what the other
 * locked); visibility requires the field be included AND not hidden by metadata.
 */
export function resolveWidget(field: FormField, meta?: FieldWidgetHint): ResolvedWidget {
  return {
    control: asControl(meta?.widget) ?? field.control,
    readOnly: field.readOnly || meta?.readOnly === true,
    visible: field.include && meta?.visible !== false,
  };
}

/** How a control maps onto a DOM element for rendering. */
export type ControlRender =
  | { kind: 'input'; type: string }
  | { kind: 'textarea' }
  | { kind: 'checkbox' }
  | { kind: 'select' };

/** Maps a control type to the element the runner should render. */
export function controlRender(control: FormControlType): ControlRender {
  switch (control) {
    case 'textarea':
      return { kind: 'textarea' };
    case 'checkbox':
      return { kind: 'checkbox' };
    case 'select':
      return { kind: 'select' };
    case 'number':
      return { kind: 'input', type: 'number' };
    case 'date':
      return { kind: 'input', type: 'date' };
    case 'datetime':
      return { kind: 'input', type: 'datetime-local' };
    default:
      return { kind: 'input', type: 'text' };
  }
}
