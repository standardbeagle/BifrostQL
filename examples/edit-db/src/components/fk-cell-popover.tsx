import { useState, type ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import { HoverCard, HoverCardTrigger, HoverCardContent } from "@/components/ui/hover-card";
import { useSchema } from "../hooks/useSchema";
import { useFetcher } from "../common/fetcher";
import type { Column } from "../types/schema";

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
    children: ReactNode;
}

function getPreviewColumns(columns: Column[]): Column[] {
    return columns
        .filter((c) => !c.isPrimaryKey)
        .slice(0, MAX_PREVIEW_COLUMNS);
}

function buildPreviewQuery(tableName: string, columns: Column[], pkColumn: string, pkType: string): string {
    const fields = columns.map((c) => c.name).join(" ");
    const gqlType = pkType === "Int" || pkType === "Int!" ? "Int" : "String";
    return `query FkPreview($id: ${gqlType}) { ${tableName}(filter: { ${pkColumn}: { _eq: $id } } limit: 1) { data { ${fields} } } }`;
}

function formatValue(value: unknown): string {
    if (value === null || value === undefined) return "-";
    if (typeof value === "boolean") return value ? "Yes" : "No";
    const str = String(value);
    if (str.length > 50) return str.slice(0, 47) + "...";
    return str;
}

export function FkCellPopover({ tableName, recordId, filterColumn, children }: FkCellPopoverProps) {
    const [open, setOpen] = useState(false);
    const schema = useSchema();
    const fetcher = useFetcher();

    const table = schema.findTable(tableName);
    const lookupType = table?.columns.find((c) => c.name === filterColumn)?.paramType ?? "Int";
    const previewColumns = table ? getPreviewColumns(table.columns) : [];

    const query = table && previewColumns.length > 0
        ? buildPreviewQuery(tableName, previewColumns, filterColumn, lookupType)
        : null;

    const numericLookupTypes = ["Int", "Int!", "Float", "Float!"];
    const variables = { id: numericLookupTypes.includes(lookupType) ? Number(recordId) : recordId };

    const { data, isLoading } = useQuery({
        queryKey: ["fkPreview", tableName, recordId],
        queryFn: () => fetcher.query<Record<string, { data: Record<string, unknown>[] }>>(query!, variables),
        enabled: open && !!query,
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
