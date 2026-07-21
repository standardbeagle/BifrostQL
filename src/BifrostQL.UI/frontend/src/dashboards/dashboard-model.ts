import type { ChartDefinition } from "../charts/chart-model";

export type DashboardTileKind = "chart" | "count" | "table";

export interface DashboardLayout { x: number; y: number; w: number; h: number; }
export interface CountTileConfig { table: string; label?: string; filter?: Record<string, unknown>; filterType?: string; }
export interface TableTileConfig { table: string; columns: string[]; limit?: number; filter?: Record<string, unknown>; filterType?: string; }
export type DashboardTileConfig = ChartDefinition | CountTileConfig | TableTileConfig;

export interface DashboardTile {
  id: string;
  kind: DashboardTileKind;
  ref?: string;
  config?: DashboardTileConfig;
  layout: DashboardLayout;
  title: string;
  refreshSeconds?: number;
}

export interface DashboardDefinition {
  kind: "bifrost.dashboard";
  version: 1;
  tiles: DashboardTile[];
  grid: { cols: number };
}

const name = /^[_A-Za-z][_0-9A-Za-z]*$/;
const positive = (value: unknown): value is number => typeof value === "number" && Number.isFinite(value) && value > 0;

/** Validate the persisted, untrusted dashboard JSON before it is ever rendered. */
export function parseDashboardDefinition(value: unknown): DashboardDefinition | null {
  if (!value || typeof value !== "object" || Array.isArray(value)) return null;
  const candidate = value as Partial<DashboardDefinition>;
  if (candidate.kind !== "bifrost.dashboard" || candidate.version !== 1 || !Array.isArray(candidate.tiles) || !candidate.grid || !positive(candidate.grid.cols)) return null;
  const tiles: DashboardTile[] = [];
  for (const raw of candidate.tiles) {
    if (!raw || typeof raw !== "object") return null;
    const tile = raw as Partial<DashboardTile>;
    if (typeof tile.id !== "string" || !tile.id || !["chart", "count", "table"].includes(tile.kind ?? "") || typeof tile.title !== "string" ||
      (!tile.ref && !tile.config) || !tile.layout || ![tile.layout.x, tile.layout.y, tile.layout.w, tile.layout.h].every((part) => typeof part === "number" && Number.isFinite(part)) ||
      tile.layout.w <= 0 || tile.layout.h <= 0 || (tile.refreshSeconds !== undefined && !positive(tile.refreshSeconds))) return null;
    tiles.push(tile as DashboardTile);
  }
  return candidate as DashboardDefinition;
}

export function blankDashboard(): DashboardDefinition {
  return { kind: "bifrost.dashboard", version: 1, tiles: [], grid: { cols: 12 } };
}

/** Schema-derived identifiers only; dashboard data never interpolates free text. */
export function assertDashboardName(value: string, description: string): void {
  if (!name.test(value)) throw new Error(`Invalid GraphQL ${description}.`);
}
