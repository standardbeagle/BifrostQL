export type DiffStrategy = 'shallow' | 'deep';

export interface DiffResult {
  changed: Record<string, unknown>;
  hasChanges: boolean;
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
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

function valuesEqual(a: unknown, b: unknown, strategy: DiffStrategy): boolean {
  if (a === b) return true;
  if (a === null || b === null) return a === b;
  if (typeof a !== typeof b) return false;

  if (Array.isArray(a) && Array.isArray(b)) {
    return arraysEqual(a, b, strategy);
  }

  if (isPlainObject(a) && isPlainObject(b)) {
    if (strategy === 'shallow') return false;
    const keysA = Object.keys(a);
    const keysB = Object.keys(b);
    if (keysA.length !== keysB.length) return false;
    return keysA.every((key) => valuesEqual(a[key], b[key], strategy));
  }

  return false;
}

export function diff(
  original: Record<string, unknown>,
  updated: Record<string, unknown>,
  strategy: DiffStrategy = 'deep',
): DiffResult {
  const changed: Record<string, unknown> = {};

  for (const key of Object.keys(updated)) {
    if (!(key in original)) {
      changed[key] = updated[key];
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
