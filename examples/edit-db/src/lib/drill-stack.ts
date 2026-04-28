/**
 * @module lib/drill-stack
 *
 * Pure-logic helpers for the stackable parent/child drill-down used by the
 * edit-db data panel. Each entry in the stack describes a side column: a
 * target table plus an optional filter (FK column + parent row id) that
 * scopes the child rows to those related to a row in the previous level.
 *
 * Kept dependency-free so it can be unit-tested without React or the DOM.
 */

import type { ColumnPanel } from '../data-panel';

/**
 * A single frame in the drill navigation chain, visible as one side column.
 */
export type DrillFrame = ColumnPanel;

/**
 * Is `frame` the same drill target as `other` (same table + same filter)?
 *
 * Used for cycle detection — drilling the same (table, filterId, filterColumn)
 * we already have on the stack is a no-op. Using strict equality on all four
 * fields is safe because they are primitives.
 */
export function framesEqual(a: DrillFrame, b: DrillFrame): boolean {
    return a.tableName === b.tableName
        && a.filterTable === b.filterTable
        && a.filterId === b.filterId
        && a.filterColumn === b.filterColumn;
}

/**
 * Push `frame` onto `stack`, guarding against:
 *   1. Exact duplicates of the top frame (no-op, returns `stack`).
 *   2. Cycles — if `frame` already appears anywhere in the stack, truncate
 *      back to that frame instead of re-pushing. This produces the natural
 *      "breadcrumb jump" behaviour when a user drills back into an ancestor.
 *
 * Returns a new array; never mutates the input.
 */
export function pushDrillFrame(stack: DrillFrame[], frame: DrillFrame): DrillFrame[] {
    const top = stack[stack.length - 1];
    if (top && framesEqual(top, frame)) return stack;

    const existingIndex = stack.findIndex((f) => framesEqual(f, frame));
    if (existingIndex >= 0) {
        // Cycle / ancestor jump — truncate to (and including) the existing entry.
        return stack.slice(0, existingIndex + 1);
    }

    return [...stack, frame];
}

/**
 * Pop entries from `stack` down to (and including) `index`.
 * Popping index 0 yields an empty stack (all side columns closed); popping
 * an out-of-range index is a no-op.
 */
export function popDrillFramesTo(stack: DrillFrame[], index: number): DrillFrame[] {
    if (index < 0) return [];
    if (index >= stack.length) return stack;
    return stack.slice(0, index);
}

/**
 * A crumb displayed in the breadcrumb bar above the drill panels.
 * `index = -1` represents the main (non-stack) view.
 */
export interface DrillCrumb {
    /** -1 for the main view, 0+ for each side-column stack entry. */
    index: number;
    /** Display label — usually the table's human label. */
    label: string;
    /** Optional secondary label showing filter context (e.g. "from Customers #42"). */
    detail?: string;
    /** The underlying frame for index >= 0 (undefined for main view). */
    frame?: DrillFrame;
}

/**
 * Resolve table labels for a drill stack and produce breadcrumb entries.
 *
 * @param mainTable - GraphQL name of the root/main table
 * @param stack     - Current drill stack
 * @param lookup    - Function returning a human label for a given GraphQL table name,
 *                    or `undefined` if not found (caller falls back to the raw name).
 */
export function buildDrillCrumbs(
    mainTable: string,
    stack: DrillFrame[],
    lookup: (tableName: string) => string | undefined,
): DrillCrumb[] {
    const labelFor = (name: string): string => lookup(name) ?? name;

    const crumbs: DrillCrumb[] = [
        { index: -1, label: labelFor(mainTable) },
    ];

    for (let i = 0; i < stack.length; i++) {
        const frame = stack[i];
        const parentName = frame.filterTable
            ? labelFor(frame.filterTable)
            : (i === 0 ? labelFor(mainTable) : labelFor(stack[i - 1].tableName));
        const detail = frame.filterId
            ? `from ${parentName} #${frame.filterId}`
            : undefined;
        crumbs.push({
            index: i,
            label: labelFor(frame.tableName),
            detail,
            frame,
        });
    }

    return crumbs;
}
