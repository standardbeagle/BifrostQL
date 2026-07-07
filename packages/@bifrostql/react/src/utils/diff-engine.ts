/**
 * Strategy for comparing object values during diff computation.
 * - `'shallow'` - Only compares top-level values by reference/identity.
 * - `'deep'` - Recursively compares nested objects and arrays.
 */
export type DiffStrategy = 'shallow' | 'deep';

/** The result of a diff operation between two objects. */
export interface DiffResult {
  /** An object containing only the fields that differ, with their new values. */
  changed: Record<string, unknown>;
  /** `true` if at least one field was changed. */
  hasChanges: boolean;
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return (
    value !== null &&
    typeof value === 'object' &&
    !Array.isArray(value) &&
    !(value instanceof Date) &&
    !(value instanceof Map) &&
    !(value instanceof Set)
  );
}

function arraysEqual(
  a: unknown[],
  b: unknown[],
  strategy: DiffStrategy,
): boolean {
  if (a.length !== b.length) return false;
  for (let i = 0; i < a.length; i++) {
    if (!valuesEqual(a[i], b[i], strategy)) return false;
  }
  return true;
}

function mapsEqual(
  a: Map<unknown, unknown>,
  b: Map<unknown, unknown>,
  strategy: DiffStrategy,
): boolean {
  if (a.size !== b.size) return false;
  for (const [key, value] of a) {
    if (!b.has(key)) return false;
    if (!valuesEqual(value, b.get(key), strategy)) return false;
  }
  return true;
}

function setsEqual(a: Set<unknown>, b: Set<unknown>): boolean {
  if (a.size !== b.size) return false;
  for (const value of a) {
    if (!b.has(value)) return false;
  }
  return true;
}

function valuesEqual(a: unknown, b: unknown, strategy: DiffStrategy): boolean {
  if (a === b) return true;
  if (a === null || b === null) return a === b;
  if (typeof a !== typeof b) return false;

  // NaN-safe number comparison: `NaN === NaN` is false, but two NaNs must count
  // as equal so a field holding NaN isn't reported as perpetually changed.
  if (typeof a === 'number') {
    return Number.isNaN(a) && Number.isNaN(b as number);
  }

  // Dates compare by timestamp. Without this they fall through to the plain
  // object branch, which sees no own enumerable keys and wrongly reports every
  // two distinct Dates as equal.
  if (a instanceof Date && b instanceof Date) {
    return a.getTime() === b.getTime();
  }
  if (a instanceof Date || b instanceof Date) return false;

  if (Array.isArray(a) || Array.isArray(b)) {
    if (!Array.isArray(a) || !Array.isArray(b)) return false;
    return arraysEqual(a, b, strategy);
  }

  if (a instanceof Map && b instanceof Map) return mapsEqual(a, b, strategy);
  if (a instanceof Map || b instanceof Map) return false;

  if (a instanceof Set && b instanceof Set) return setsEqual(a, b);
  if (a instanceof Set || b instanceof Set) return false;

  if (isPlainObject(a) && isPlainObject(b)) {
    if (strategy === 'shallow') return false;
    const keysA = Object.keys(a);
    const keysB = Object.keys(b);
    if (keysA.length !== keysB.length) return false;
    return keysA.every(
      (key) =>
        Object.prototype.hasOwnProperty.call(b, key) &&
        valuesEqual(a[key], b[key], strategy),
    );
  }

  return false;
}

/**
 * Compute the fields that changed between two objects.
 *
 * The union of keys is compared. A field present only in `updated` is reported
 * with its new value; a field present only in `original` (removed) is reported
 * as `null` so the caller can clear it server-side.
 *
 * @param original - The original (baseline) object.
 * @param updated - The modified object.
 * @param strategy - Comparison strategy: `'shallow'` or `'deep'` (default).
 * @returns A {@link DiffResult} with the changed fields and a `hasChanges` flag.
 *
 * @example
 * ```ts
 * const result = diff(
 *   { name: 'Alice', age: 30 },
 *   { name: 'Alice', age: 31 },
 * );
 * // result.changed = { age: 31 }, result.hasChanges = true
 * ```
 */
export function diff(
  original: Record<string, unknown>,
  updated: Record<string, unknown>,
  strategy: DiffStrategy = 'deep',
): DiffResult {
  const changed: Record<string, unknown> = {};

  const keys = new Set([...Object.keys(original), ...Object.keys(updated)]);
  for (const key of keys) {
    const inOriginal = Object.prototype.hasOwnProperty.call(original, key);
    const inUpdated = Object.prototype.hasOwnProperty.call(updated, key);

    if (inUpdated && !inOriginal) {
      // Added field.
      changed[key] = updated[key];
      continue;
    }
    if (inOriginal && !inUpdated) {
      // Removed field — report as null so it can be cleared server-side.
      changed[key] = null;
      continue;
    }
    if (!valuesEqual(original[key], updated[key], strategy)) {
      changed[key] = updated[key];
    }
  }

  return {
    changed,
    hasChanges: Object.keys(changed).length > 0,
  };
}

/**
 * Detect three-way merge conflicts between a base state, the current server
 * state, and incoming local changes.
 *
 * A conflict occurs when both `current` and `incoming` changed the same field
 * relative to `base`, but to different values.
 *
 * @param base - The common ancestor state.
 * @param current - The current server state (may have been modified by others).
 * @param incoming - The local changes being submitted.
 * @returns An array of conflicting field names, empty if no conflicts.
 *
 * @example
 * ```ts
 * const conflicts = detectConflicts(
 *   { name: 'Alice', age: 30 },
 *   { name: 'Alice', age: 31 },  // server changed age
 *   { name: 'Alice', age: 32 },  // local also changed age
 * );
 * // conflicts = ['age']
 * ```
 */
export function detectConflicts(
  base: Record<string, unknown>,
  current: Record<string, unknown>,
  incoming: Record<string, unknown>,
): string[] {
  const conflicts: string[] = [];
  const currentDiff = diff(base, current);
  const incomingDiff = diff(base, incoming);

  for (const key of Object.keys(currentDiff.changed)) {
    if (
      key in incomingDiff.changed &&
      !valuesEqual(currentDiff.changed[key], incomingDiff.changed[key], 'deep')
    ) {
      conflicts.push(key);
    }
  }

  return conflicts;
}
