/** Whether a `window` global exists (false during SSR). */
export function canAccessWindow(): boolean {
  return typeof window !== 'undefined';
}
