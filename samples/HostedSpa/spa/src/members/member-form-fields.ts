import type { EntityMetadata, FieldMetadata } from '@bifrostql/app-shell';

/** Permission that grants visibility of admin-only (`visible: false`) fields. */
export const MEMBERS_ADMIN = 'main.members.admin';

/**
 * A metadata-driven field descriptor for the member form / detail view.
 *
 * Produced by {@link buildMemberFormFields}, which combines each field's
 * overlay {@link FieldMetadata} with the current session's permissions to
 * decide whether the field is shown at all and whether it is editable.
 */
export interface MemberFormField {
  /** Field name (overlay key, used as the GraphQL column name). */
  name: string;
  /** Field-level overlay metadata driving widget selection and presentation. */
  field: FieldMetadata;
  /**
   * `true` when the field is gated behind `visible: false` in the overlay —
   * i.e. an admin-only field. Surfaced so the UI can flag it (e.g. a badge)
   * and so tests can assert the permission gate without re-reading metadata.
   */
  adminOnly: boolean;
  /**
   * `true` when the field must not be edited: either the overlay marks it
   * `readOnly`, or it is an admin-only field rendered for a non-admin (which
   * only happens in the detail view, never the editable form).
   */
  readOnly: boolean;
}

/**
 * Derive the ordered set of member-form fields from entity metadata, gated by
 * the current session's permissions.
 *
 * Field order prefers the entity's `displayFields`, then any remaining
 * `fields` in declaration order, so the form layout is entirely overlay-driven
 * rather than hardcoded.
 *
 * Permission gate: a field with `visible: false` in the overlay is *admin-only*
 * (sub-task 1 established `visible: false` as the permission/visibility
 * mechanism — it gates `user_id`, `tenant_id`, `deleted_at`). Such a field is
 * omitted entirely unless the session holds {@link MEMBERS_ADMIN}; for an admin
 * it is included and flagged `adminOnly`. Read-only is the union of the
 * overlay's `readOnly` flag and the admin-only-for-non-admin case.
 *
 * @param entity - The `main.members` entity metadata from the overlay.
 * @param permissions - The current session's permission strings.
 */
export function buildMemberFormFields(
  entity: EntityMetadata,
  permissions: string[],
): MemberFormField[] {
  const fields: Record<string, FieldMetadata> = entity.fields ?? {};
  const isAdmin = permissions.includes(MEMBERS_ADMIN);

  const ordered = [
    ...(entity.displayFields ?? []),
    ...Object.keys(fields).filter(
      (name) => !(entity.displayFields ?? []).includes(name),
    ),
  ];

  return ordered
    .map((name): MemberFormField | null => {
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
        readOnly: field.readOnly === true,
      };
    })
    .filter((entry): entry is MemberFormField => entry !== null);
}
