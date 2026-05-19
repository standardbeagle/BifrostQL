import { useState, type ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import { HoverCard, HoverCardTrigger, HoverCardContent } from "@/components/ui/hover-card";
import { useSchema } from "../hooks/useSchema";
import { useFetcher } from "../common/fetcher";
import type { Column, Join, Table } from "../types/schema";
import { buildFkEqFilter, isComposite } from "../lib/fk";

const MAX_PREVIEW_COLUMNS = 5;
const HOVER_OPEN_DELAY = 400;
const HOVER_CLOSE_DELAY = 200;

interface FkCellPopoverProps {
    tableName: string;
    recordId: string | number;
    /**
     * The column on `tableName` whose value is `recordId`. Callers pass the FK target
     * column from the join metadata (`destinationColumnNames[0]`). Using the target's
     * "primary key" column here is wrong for composite-PK and denormalized-FK targets.
     */
    filterColumn: string;
    /**
     * The full join descriptor. When the join is composite, the popover ignores
     * `recordId`/`filterColumn` and builds an `{and: [...]}` filter from every
     * `sourceColumnNames` value on `sourceRow`. Single-FK callers can omit this.
     */
    join?: Join;
    /**
     * The host row that owns the FK cell. Required only for composite FKs so that
     * the popover can read all source-side column values to rebuild the filter.
     */
    sourceRow?: Record<string, unknown>;
    children: ReactNode;
}

function getPreviewColumns(columns: Column[]): Column[] {
    return columns
        .filter((c) => !c.isPrimaryKey)
        .slice(0, MAX_PREVIEW_COLUMNS);
}

function buildSinglePreviewQuery(tableName: string, columns: Column[], pkColumn: string, pkType: string): string {
    const fields = columns.map((c) => c.name).join(" ");
    const gqlType = pkType === "Int" || pkType === "Int!" ? "Int" : "String";
    return `query FkPreview($id: ${gqlType}) { ${tableName}(filter: { ${pkColumn}: { _eq: $id } } limit: 1) { data { ${fields} } } }`;
}

function buildCompositePreviewQuery(tableName: string, columns: Column[], paramsDecl: string, filterText: string): string {
    const fields = columns.map((c) => c.name).join(" ");
    return `query FkPreview(${paramsDecl}) { ${tableName}(filter: ${filterText} limit: 1) { data { ${fields} } } }`;
}

function formatValue(value: unknown): string {
    if (value === null || value === undefined) return "-";
    if (typeof value === "boolean") return value ? "Yes" : "No";
    const str = String(value);
    if (str.length > 50) return str.slice(0, 47) + "...";
    return str;
}

interface PreviewPlan {
    query: string;
    variables: Record<string, unknown>;
    cacheKey: unknown;
}

function buildPreviewPlan(
    tableName: string,
    destTable: Table,
    previewColumns: Column[],
    join: Join | undefined,
    sourceRow: Record<string, unknown> | undefined,
    recordId: string | number,
    filterColumn: string,
): PreviewPlan | null {
    if (previewColumns.length === 0) return null;
    if (join && isComposite(join) && sourceRow) {
        const f = buildFkEqFilter(sourceRow, join, destTable);
        if (!f) return null;
        const paramsDecl = f.params.join(", ");
        return {
            query: buildCompositePreviewQuery(tableName, previewColumns, paramsDecl, f.filterText),
            variables: f.variables,
            cacheKey: f.variables,
        };
    }
    const lookupType = destTable.columns.find((c) => c.name === filterColumn)?.paramType ?? "Int";
    const numericLookupTypes = ["Int", "Int!", "Float", "Float!"];
    const variables = { id: numericLookupTypes.includes(lookupType) ? Number(recordId) : recordId };
    return {
        query: buildSinglePreviewQuery(tableName, previewColumns, filterColumn, lookupType),
        variables,
        cacheKey: recordId,
    };
}

export function FkCellPopover({ tableName, recordId, filterColumn, join, sourceRow, children }: FkCellPopoverProps) {
    const [open, setOpen] = useState(false);
    const schema = useSchema();
    const fetcher = useFetcher();

    const table = schema.findTable(tableName);
    const previewColumns = table ? getPreviewColumns(table.columns) : [];

    const plan = table
        ? buildPreviewPlan(tableName, table, previewColumns, join, sourceRow, recordId, filterColumn)
        : null;

    const { data, isLoading } = useQuery({
        queryKey: ["fkPreview", tableName, plan?.cacheKey],
        queryFn: () => fetcher.query<Record<string, { data: Record<string, unknown>[] }>>(plan!.query, plan!.variables),
        enabled: open && !!plan,
        staleTime: 5 * 60 * 1000,
    });

    const record = data?.[tableName]?.data?.[0];

    return (
        <HoverCard openDelay={HOVER_OPEN_DELAY} closeDelay={HOVER_CLOSE_DELAY} open={open} onOpenChange={setOpen}>
            <HoverCardTrigger asChild>
                {children}
            </HoverCardTrigger>
            <HoverCardContent align="start" className="w-72">
                <div className="space-y-2">
                    <p className="text-sm font-semibold text-foreground">{table?.label ?? tableName}</p>
                    {isLoading && <p className="text-xs text-muted-foreground">Loading...</p>}
                    {record && (
                        <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-xs">
                            {previewColumns.map((col) => (
                                <div key={col.name} className="contents">
                                    <dt className="text-muted-foreground truncate">{col.label}</dt>
                                    <dd className="text-foreground truncate">{formatValue(record[col.name])}</dd>
                                </div>
                            ))}
                        </dl>
                    )}
                    {!isLoading && !record && open && (
                        <p className="text-xs text-muted-foreground">Record not found</p>
                    )}
                </div>
            </HoverCardContent>
        </HoverCard>
    );
}
