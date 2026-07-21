import ELK from 'elkjs/lib/elk.bundled.js';
import type { Edge, Node } from '@xyflow/react';
import type { ErdJoin as Join, ErdTable as Table } from './types';

export type RelationshipKind = 'foreign-key' | 'many-to-many' | 'name-based' | 'polymorphic';
export type ErdEdge = Edge<{ kind: RelationshipKind; columns: string; annotation?: string }>;

export interface ErdGraph { nodes: Node<Table>[]; edges: ErdEdge[]; }

type RelationshipJoin = Join;

function joinKind(join: RelationshipJoin): RelationshipKind {
  if (join.isPolymorphic) return 'polymorphic';
  // Older schemas expose no provenance for a join. Newer fixtures/servers may
  // send it as metadata; keep that signal rather than guessing from table names.
  if (join.relationshipKind === 'name-based' || join.metadata?.relationshipKind === 'name-based') return 'name-based';
  return 'foreign-key';
}

/** Convert the edit-db `_dbSchema` relationship shape into a diagram graph. */
export function mapSchemaToErd(tables: Table[]): ErdGraph {
  const junctions = new Set(tables.flatMap((table) => (table.manyToManyJoins ?? []).map((m) => m.junctionTable)));
  const nodes = tables.filter((table) => !junctions.has(table.name)).map((table) => ({ id: table.name, type: 'table', position: { x: 0, y: 0 }, data: table }));
  const visible = new Set(nodes.map((node) => node.id));
  const edges: ErdEdge[] = [];
  const seen = new Set<string>();
  for (const table of tables) {
    for (const rawJoin of table.singleJoins as RelationshipJoin[]) {
      if (!visible.has(table.name) || !visible.has(rawJoin.destinationTable)) continue;
      const kind = joinKind(rawJoin);
      const key = `${table.name}:${rawJoin.destinationTable}:${rawJoin.sourceColumnNames.join(',')}:${kind}`;
      if (seen.has(key)) continue;
      seen.add(key);
      edges.push({ id: key, source: table.name, target: rawJoin.destinationTable, animated: kind === 'polymorphic', style: kind === 'name-based' ? { strokeDasharray: '5 4' } : undefined, label: kind === 'polymorphic' ? `polymorphic: ${rawJoin.polymorphicTypeColumn ?? 'type'}` : undefined, markerEnd: { type: 'arrowclosed' }, data: { kind, columns: `${rawJoin.sourceColumnNames.join(', ')} → ${rawJoin.destinationColumnNames.join(', ')}`, annotation: rawJoin.polymorphicTypeValue } });
    }
    for (const m2m of table.manyToManyJoins ?? []) {
      if (!visible.has(table.name) || !visible.has(m2m.targetTable)) continue;
      const pair = [table.name, m2m.targetTable].sort().join(':');
      const key = `m2m:${pair}`;
      if (seen.has(key)) continue;
      seen.add(key);
      edges.push({ id: key, source: table.name, target: m2m.targetTable, style: { strokeDasharray: '8 4' }, label: `M2M via ${m2m.junctionTable}`, data: { kind: 'many-to-many', columns: `${m2m.junctionSourceColumnNames.join(', ')} → ${m2m.junctionTargetColumnNames.join(', ')}` } });
    }
  }
  return { nodes, edges };
}

const elk = new ELK();
/** Layered ELK placement, kept pure so large-schema performance is testable. */
export async function layoutErd(graph: ErdGraph): Promise<ErdGraph> {
  const layout = await elk.layout({ id: 'erd', layoutOptions: { 'elk.algorithm': 'layered', 'elk.direction': 'RIGHT', 'elk.spacing.nodeNode': '36', 'elk.layered.spacing.nodeNodeBetweenLayers': '80' }, children: graph.nodes.map((n) => ({ id: n.id, width: 220, height: 90 })), edges: graph.edges.map((e) => ({ id: e.id, sources: [e.source], targets: [e.target] })) });
  const positions = new Map((layout.children ?? []).map((child) => [child.id, { x: child.x ?? 0, y: child.y ?? 0 }]));
  return { ...graph, nodes: graph.nodes.map((node) => ({ ...node, position: positions.get(node.id) ?? node.position })) };
}

export function schemaName(table: Table): string { return table.dbName.includes('.') ? table.dbName.split('.')[0] : 'default'; }

/** Restrict a graph to a selected table and its N-hop neighbours. */
export function neighborhood(graph: ErdGraph, selected: string, hops: number): ErdGraph {
  const visible = new Set([selected]);
  for (let step = 0; step < hops; step++) {
    // Expand one ring at a time. Iterating directly over a mutating set turns a
    // single hop into a whole connected component when edges are ordered as a chain.
    const ring = new Set(visible);
    for (const edge of graph.edges) {
      if (ring.has(edge.source)) visible.add(edge.target);
      if (ring.has(edge.target)) visible.add(edge.source);
    }
  }
  return { nodes: graph.nodes.filter((node) => visible.has(node.id)), edges: graph.edges.filter((edge) => visible.has(edge.source) && visible.has(edge.target)) };
}
