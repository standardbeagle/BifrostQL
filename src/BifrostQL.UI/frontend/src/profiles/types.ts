export interface ApiProfile {
  id: string;
  label: string;
  /** Server module profile name; null/undefined = raw default (no ?profile=). */
  serverProfile?: string | null;
}
