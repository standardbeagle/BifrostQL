import { useEffect, useMemo, useState } from 'react';
import { BaseEdge, EdgeLabelRenderer, ReactFlow, Background, Controls, MiniMap, getBezierPath, type EdgeProps, type NodeProps } from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import type { GraphQLFetcher } from '@standardbeagle/edit-db';
import type { ErdSchemaTable, ErdTable as Table } from './types';
import { layoutErd, mapSchemaToErd, neighborhood, schemaName, type ErdEdge, type ErdGraph } from './model';

export const GET_ERD_SCHEMA = `query ErdSchema { _dbSchema { dbName graphQlName labelColumn primaryKeys isEditable metadata { key value } columns { dbName graphQlName paramType dbType isPrimaryKey isIdentity isNullable isReadOnly metadata { key value } } multiJoins { name fieldName sourceColumnNames destinationTable destinationColumnNames relationshipKind isPolymorphic polymorphicTypeColumn polymorphicTypeValue } singleJoins { name fieldName sourceColumnNames destinationTable destinationColumnNames relationshipKind isPolymorphic polymorphicTypeColumn polymorphicTypeValue } manyToManyJoins { name targetTable junctionTable junctionTargetField sourceColumnNames junctionSourceColumnNames junctionTargetColumnNames targetColumnNames hasPayload } } }`;

const humanizeName = (name: string) => name.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/[_-]+/g, ' ').replace(/\b\w/g, (letter) => letter.toUpperCase());

/** Matches edit-db's schema normalization before graph mapping consumes it. */
export function normalizeErdSchema(tables: ErdSchemaTable[]): Table[] {
  return tables.map((table) => ({
    ...table,
    name: table.graphQlName,
    label: humanizeName(table.dbName),
    columns: table.columns.map((column) => ({ ...column, name: column.graphQlName })),
  }));
}

function TableNode({ data }: NodeProps) {
  const { table, onOpenTable } = data as unknown as { table: Table; onOpenTable: (name: string) => void };
  const [expanded, setExpanded] = useState(false);
  return <div className="erd-table-node"><button type="button" onClick={() => onOpenTable(table.name)} title={`Open ${table.name} in editor`}><strong>{table.label || table.name}</strong><span className="erd-pk">PK {table.primaryKeys.join(', ') || '—'}</span></button><button type="button" onClick={() => setExpanded((value) => !value)} aria-expanded={expanded}>Columns</button>{expanded && <ul>{table.columns.map((column) => <li key={column.name}>{column.name}</li>)}</ul>}</div>;
}

/** A focusable edge keeps the relationship's join mapping discoverable without editing it. */
function RelationshipEdge({ data, sourceX, sourceY, sourcePosition, targetX, targetY, targetPosition, markerEnd, style }: EdgeProps) {
  const [showColumns, setShowColumns] = useState(false);
  const [path, labelX, labelY] = getBezierPath({ sourceX, sourceY, sourcePosition, targetX, targetY, targetPosition });
  const columns = (data as ErdEdge['data'] | undefined)?.columns ?? 'Join columns unavailable';
  return <>
    <BaseEdge path={path} markerEnd={markerEnd} style={style} />
    <path d={path} fill="none" stroke="transparent" strokeWidth={20} tabIndex={0} role="img" aria-label={`Relationship join columns: ${columns}`} onMouseEnter={() => setShowColumns(true)} onMouseLeave={() => setShowColumns(false)} onFocus={() => setShowColumns(true)} onBlur={() => setShowColumns(false)} />
    {showColumns && <EdgeLabelRenderer><div className="erd-edge-tooltip" role="tooltip" style={{ transform: `translate(-50%, -50%) translate(${labelX}px,${labelY}px)` }}>Join columns: {columns}</div></EdgeLabelRenderer>}
  </>;
}

const edgeTypes = { relationship: RelationshipEdge };

export interface ErdPaneProps { fetcher: GraphQLFetcher; onOpenTable: (name: string) => void; }
export function ErdPane({ fetcher, onOpenTable }: ErdPaneProps) {
  const [graph, setGraph] = useState<ErdGraph>({ nodes: [], edges: [] });
  const [filter, setFilter] = useState('');
  const [hops, setHops] = useState(1);
  const [cluster, setCluster] = useState('');
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { let active = true; fetcher.query<{ _dbSchema: ErdSchemaTable[] }>(GET_ERD_SCHEMA).then((result) => layoutErd(mapSchemaToErd(normalizeErdSchema(result._dbSchema)))).then((next) => { if (active) setGraph(next); }).catch((reason: Error) => { if (active) setError(reason.message); }); return () => { active = false; }; }, [fetcher]);
  const schemaNames = useMemo(() => [...new Set(graph.nodes.map((node) => schemaName(node.data)))], [graph]);
  const clustered = useMemo(() => cluster ? { nodes: graph.nodes.filter((node) => schemaName(node.data) === cluster), edges: graph.edges.filter((edge) => graph.nodes.find((node) => node.id === edge.source && schemaName(node.data) === cluster) && graph.nodes.find((node) => node.id === edge.target && schemaName(node.data) === cluster)) } : graph, [cluster, graph]);
  const display = useMemo(() => filter ? neighborhood(clustered, filter, hops) : clustered, [clustered, filter, hops]);
  const nodes = useMemo(() => display.nodes.map((node) => ({ ...node, data: { table: node.data, onOpenTable } })), [display.nodes, onOpenTable]);
  const edges = useMemo(() => display.edges.map((edge) => ({ ...edge, type: 'relationship' })), [display.edges]);
  const exportPng = async () => { const svg = document.querySelector('.react-flow__viewport svg') as SVGSVGElement | null; if (!svg) return; const blob = new Blob([new XMLSerializer().serializeToString(svg)], { type: 'image/svg+xml' }); const image = new Image(); image.onload = () => { const canvas = document.createElement('canvas'); canvas.width = image.width || 1200; canvas.height = image.height || 800; canvas.getContext('2d')?.drawImage(image, 0, 0); const link = document.createElement('a'); link.download = 'bifrostql-erd.png'; link.href = canvas.toDataURL('image/png'); link.click(); }; image.src = URL.createObjectURL(blob); };
  if (error) return <div role="alert">Could not load diagram: {error}</div>;
  return <section className="erd-pane"><div className="erd-toolbar"><label>Table <select value={filter} onChange={(event) => setFilter(event.target.value)}><option value="">All tables</option>{graph.nodes.map((node) => <option key={node.id} value={node.id}>{node.data.label || node.id}</option>)}</select></label><label>N-hop <input aria-label="N-hop neighbors" type="number" min="1" max="10" value={hops} onChange={(event) => setHops(Number(event.target.value))} /></label>{graph.nodes.length > 100 && <label>Schema cluster <select value={cluster} onChange={(event) => setCluster(event.target.value)}><option value="">All schemas</option>{schemaNames.map((name) => <option key={name}>{name}</option>)}</select></label>}<button type="button" onClick={() => void exportPng()}>Export PNG</button></div><div className="erd-canvas"><ReactFlow nodes={nodes} edges={edges} nodeTypes={{ table: TableNode }} edgeTypes={edgeTypes} fitView><MiniMap /><Controls /><Background /></ReactFlow></div></section>;
}
