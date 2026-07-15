import { useCallback, useRef, useState } from 'react';
import { Button } from './ui/button';
import {
    DropdownMenu,
    DropdownMenuTrigger,
    DropdownMenuContent,
    DropdownMenuItem,
} from './ui/dropdown-menu';
import { ChevronDown, Download, X } from 'lucide-react';
import { useToast } from '../hooks/useToast';
import {
    downloadTextFile,
    filenameFor,
    mimeFor,
    DEFAULT_ROW_CAP,
    type ExportFormat,
    type ExportRunner,
} from '../lib/export';

interface ExportButtonProps {
    /** Bound runner that pages the full filtered/sorted result set. */
    exportRows: ExportRunner;
    /** Total matching rows — drives the above-cap confirmation. */
    total: number;
    /** Base name for the downloaded file. */
    tableName: string;
    /** Above this many rows the export prompts for confirmation. */
    rowCap?: number;
}

/**
 * Toolbar control that exports the full result set through the shared export
 * util. Above `rowCap` it confirms first; while running it swaps to a Cancel
 * button that aborts paging (leaving no file). Cancellation is silent — no error
 * toast — since the user asked for it.
 */
export function ExportButton({
    exportRows,
    total,
    tableName,
    rowCap = DEFAULT_ROW_CAP,
}: ExportButtonProps) {
    const { toast } = useToast();
    const [running, setRunning] = useState(false);
    const [fetched, setFetched] = useState(0);
    const abortRef = useRef<AbortController | null>(null);

    const start = useCallback(
        async (format: ExportFormat) => {
            if (running) return;
            // Confirm before draining a very large result set out of the database.
            if (
                total > rowCap &&
                typeof window !== 'undefined' &&
                !window.confirm(
                    `This export has ${total.toLocaleString()} rows, over the ` +
                        `${rowCap.toLocaleString()} row limit. Only the first ` +
                        `${rowCap.toLocaleString()} rows will be exported. Continue?`,
                )
            ) {
                return;
            }
            const controller = new AbortController();
            abortRef.current = controller;
            setRunning(true);
            setFetched(0);
            try {
                const result = await exportRows({
                    format,
                    signal: controller.signal,
                    rowCap,
                    onProgress: (count) => setFetched(count),
                });
                if (!result) return;
                downloadTextFile(
                    result.content,
                    filenameFor(tableName, format),
                    mimeFor(format),
                );
                if (result.truncated) {
                    toast(
                        `Export truncated at ${result.rowCount.toLocaleString()} rows.`,
                        'error',
                    );
                }
            } catch (err) {
                // A cancel aborts the paging and produces no file — not an error.
                if (controller.signal.aborted) return;
                toast(
                    `Export failed: ${err instanceof Error ? err.message : String(err)}`,
                    'error',
                );
            } finally {
                setRunning(false);
                abortRef.current = null;
            }
        },
        [running, total, rowCap, exportRows, tableName, toast],
    );

    const cancel = useCallback(() => abortRef.current?.abort(), []);

    if (running) {
        return (
            <Button
                variant="outline"
                size="sm"
                onClick={cancel}
                title="Cancel export"
            >
                <X className="size-3.5" />
                Cancel ({fetched.toLocaleString()})
            </Button>
        );
    }

    return (
        <DropdownMenu>
            <DropdownMenuTrigger asChild>
                <Button variant="outline" size="sm">
                    <Download className="size-3.5" />
                    Export
                    <ChevronDown className="size-3.5" />
                </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end">
                <DropdownMenuItem onClick={() => void start('csv')}>
                    CSV
                </DropdownMenuItem>
                <DropdownMenuItem onClick={() => void start('json')}>
                    JSON
                </DropdownMenuItem>
            </DropdownMenuContent>
        </DropdownMenu>
    );
}
