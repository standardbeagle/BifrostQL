import type { ComponentType } from 'react';
import type { Table } from '../types/schema';

/**
 * A host-contributed action on a TABLE in the navigation list. The editor renders
 * every action in each table's kebab menu; what the action does is entirely the
 * host's business (open a query designer, copy a connection string, …) — edit-db
 * stays transport- and host-agnostic and just invokes the callback with the
 * schema table. Built-in actions (Download CSV/JSON) use this same shape, so a
 * host's contributions and the built-ins render as one uniform menu.
 */
export interface TableAction {
    /** Stable identity, e.g. `edit-as-query`. */
    id: string;
    /** Menu item label. */
    label: string;
    /** Optional 16px icon component (lucide-style: takes className). */
    icon?: ComponentType<{ className?: string }>;
    /** Hides the action for tables it does not apply to. Absent means always shown. */
    enabled?: (table: Table) => boolean;
    /** Invoked with the schema table when the user picks the action. */
    onSelect: (table: Table) => void | Promise<void>;
}
