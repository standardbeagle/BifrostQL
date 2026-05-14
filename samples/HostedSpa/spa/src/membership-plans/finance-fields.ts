/**
 * Finance-field read-gating for the SPA.
 *
 * The host enforces `policy-read-deny` on the finance columns
 * (`membership_plans.price_cents`, `dues_invoices.amount_cents`,
 * `dues_payments.amount_cents`) qualified to
 * `policy-read-deny-roles: officer,event_manager,member,read_only` — a direct
 * GraphQL query for those columns by a non-finance role is rejected by the
 * server. This module is the SPA-side mirror of that policy: it lets the
 * affected screens hide those columns from non-finance sessions so they never
 * issue a query the server would reject.
 *
 * The gate is a session-permission check — `finance_manager` and `admin` hold
 * {@link MEMBERS_FINANCE}; `officer`, `event_manager`, `member`, and
 * `read_only` do not. This mirrors how `main.members.write` / `.admin` gate
 * actions and admin-only fields elsewhere in the SPA, rather than reusing the
 * overlay's `visible: false` mechanism (which keys on `main.members.admin`
 * only and would over-restrict `finance_manager`).
 */

/**
 * Permission that grants visibility of finance columns (`price_cents`,
 * `amount_cents`). Held by `finance_manager` and `admin` sessions; not held by
 * `officer`, `event_manager`, `member`, or `read_only`.
 *
 * Mirrors the server's `policy-read-deny-roles` set: the roles *without* this
 * permission are exactly the roles the host denies finance-column reads to.
 */
export const MEMBERS_FINANCE = 'main.members.finance';

/**
 * The finance column names gated by {@link MEMBERS_FINANCE}, keyed by the
 * overlay entity that declares them. These are exactly the columns the host
 * carries a `policy-read-deny` on.
 */
export const FINANCE_FIELDS_BY_ENTITY: Record<string, readonly string[]> = {
  'main.membership_plans': ['price_cents'],
  'main.dues_invoices': ['amount_cents'],
  'main.dues_payments': ['amount_cents'],
};

/** Every gated finance column name, across all entities. */
const ALL_FINANCE_FIELDS: readonly string[] = Array.from(
  new Set(Object.values(FINANCE_FIELDS_BY_ENTITY).flat()),
);

/**
 * `true` when the session may read finance columns — i.e. it holds
 * {@link MEMBERS_FINANCE}.
 *
 * @param permissions - The current session's permission strings.
 */
export function canReadFinanceFields(permissions: string[]): boolean {
  return permissions.includes(MEMBERS_FINANCE);
}

/**
 * `true` when `fieldName` is a gated finance column. When `entityKey` is given,
 * the check is scoped to that entity's finance columns; otherwise any entity's
 * finance column matches.
 *
 * @param fieldName - The overlay field / GraphQL column name.
 * @param entityKey - Optional qualified entity key to scope the check.
 */
export function isFinanceField(fieldName: string, entityKey?: string): boolean {
  if (entityKey) {
    return (FINANCE_FIELDS_BY_ENTITY[entityKey] ?? []).includes(fieldName);
  }
  return ALL_FINANCE_FIELDS.includes(fieldName);
}

/**
 * Filter a list of field / column names down to those the session may read:
 * for a finance session the list is unchanged; for a non-finance session every
 * finance column for `entityKey` is dropped.
 *
 * Use this to gate `defaultColumns`, query field lists, and form-field sets in
 * the finance screens so a non-finance session never names a denied column.
 *
 * @param names - Candidate field / column names.
 * @param entityKey - Qualified entity key the names belong to.
 * @param permissions - The current session's permission strings.
 */
export function gateFinanceFields(
  names: string[],
  entityKey: string,
  permissions: string[],
): string[] {
  if (canReadFinanceFields(permissions)) {
    return names;
  }
  return names.filter((name) => !isFinanceField(name, entityKey));
}
