import type { EntityMetadata, FieldMetadata } from '@bifrostql/app-shell';

/** Permission that grants visibility of admin-only (`visible: false`) fields. */
export const MEMBERS_ADMIN = 'main.members.admin';

/**
 * A metadata-driven field descriptor for the event form / detail view.
 *
 * Produced by {@link buildEventFormFields}, which combines each field's overlay
 * {@link FieldMetadata} with the current session's permissions to decide
 * whether the field is shown at all and whether it is editable. Mirrors
 * `buildPlanFormFields` so the event form stays consistent with the plan and
 * member form screens.
 */
export interface EventFormField {
  /** Field name (overlay key, used as the GraphQL column name). */
  name: string;
  /** Field-level overlay metadata driving widget selection and presentation. */
  field: FieldMetadata;
  /**
   * `true` when the field is gated behind `visible: false` in the overlay —
   * i.e. an admin-only field (e.g. `created_at`, `tenant_id`). Surfaced so the
   * UI can flag it and tests can assert the permission gate without re-reading
   * metadata.
   */
  adminOnly: boolean;
  /**
   * `true` when the field must not be edited: either the overlay marks it
   * `readOnly`, or it is an admin-only field rendered for a non-admin.
   */
  readOnly: boolean;
}

/**
 * Derive the ordered set of event-form fields from entity metadata, gated by
 * the current session's permissions.
 *
 * Field order prefers the entity's `displayFields`, then any remaining `fields`
 * in declaration order, so the form layout is entirely overlay-driven rather
 * than hardcoded. A field with `visible: false` in the overlay is *admin-only*
 * (`created_at`, `tenant_id`): omitted entirely unless the session holds
 * {@link MEMBERS_ADMIN}; for an admin it is included and flagged `adminOnly`,
 * always read-only.
 *
 * @param entity - The `main.events` entity metadata from the overlay.
 * @param permissions - The current session's permission strings.
 */
export function buildEventFormFields(
  entity: EntityMetadata,
  permissions: string[],
): EventFormField[] {
  const fields: Record<string, FieldMetadata> = entity.fields ?? {};
  const isAdmin = permissions.includes(MEMBERS_ADMIN);

  const ordered = [
    ...(entity.displayFields ?? []),
    ...Object.keys(fields).filter(
      (name) => !(entity.displayFields ?? []).includes(name),
    ),
  ];

  return ordered
    .map((name): EventFormField | null => {
      const field = fields[name];
      if (!field) {
        return null;
      }
      const adminOnly = field.visible === false;
      if (adminOnly && !isAdmin) {
        return null;
      }
      return {
        name,
        field,
        adminOnly,
        readOnly: adminOnly || field.readOnly === true,
      };
    })
    .filter((entry): entry is EventFormField => entry !== null);
}
