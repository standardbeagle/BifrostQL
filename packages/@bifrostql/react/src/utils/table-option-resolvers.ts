import type {
  ClientSideFilterConfig,
  ClientSideSortConfig,
  UrlSyncConfig,
} from '../hooks/use-bifrost-table.types';

/**
 * Normalizers for `useBifrostTable` options that accept a boolean shorthand or
 * a config object; each resolver returns the fully-defaulted config shape.
 */

export function resolveUrlSyncConfig(
  urlSync: boolean | UrlSyncConfig | undefined,
): {
  enabled: boolean;
  prefix: string;
  debounceMs: number;
} {
  if (urlSync === false)
    return { enabled: false, prefix: 'table', debounceMs: 500 };
  if (urlSync === true || urlSync === undefined) {
    return { enabled: true, prefix: 'table', debounceMs: 500 };
  }
  return {
    enabled: urlSync.enabled !== false,
    prefix: urlSync.prefix ?? 'table',
    debounceMs: urlSync.debounceMs ?? 500,
  };
}

export function resolveClientSideSortConfig(
  config: boolean | ClientSideSortConfig | undefined,
): { enabled: boolean; threshold: number } {
  if (config === true) return { enabled: true, threshold: Infinity };
  if (config === false || config === undefined)
    return { enabled: false, threshold: 0 };
  return {
    enabled: config.enabled,
    threshold: config.threshold ?? Infinity,
  };
}

export function resolveClientSideFilterConfig(
  config: boolean | ClientSideFilterConfig | undefined,
): { enabled: boolean; threshold: number } {
  if (config === true) return { enabled: true, threshold: Infinity };
  if (config === false || config === undefined)
    return { enabled: false, threshold: 0 };
  return {
    enabled: config.enabled,
    threshold: config.threshold ?? Infinity,
  };
}
