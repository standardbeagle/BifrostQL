import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import React from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { ExportButton } from './export-button';
import * as exportLib from '../lib/export';
import { ToastProvider } from '../hooks/useToast';
import type { ExportResult, RunExportOptions } from '../lib/export';

// Render the Radix dropdown inline (no portal/pointer-capture) so the CSV/JSON
// items are directly clickable under jsdom — the same approach the other
// component tests take for this menu.
vi.mock('./ui/dropdown-menu', () => ({
    DropdownMenu: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
    DropdownMenuTrigger: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
    DropdownMenuContent: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
    DropdownMenuItem: ({ children, onClick }: { children: React.ReactNode; onClick?: () => void }) => (
        <button type="button" onClick={onClick}>{children}</button>
    ),
}));

function renderButton(props: Parameters<typeof ExportButton>[0]) {
    return render(
        <ToastProvider>
            <ExportButton {...props} />
        </ToastProvider>,
    );
}

describe('ExportButton', () => {
    beforeEach(() => {
        // downloadTextFile touches the DOM/URL; stub it so we can assert on calls.
        vi.spyOn(exportLib, 'downloadTextFile').mockImplementation(() => {});
    });
    afterEach(() => {
        vi.restoreAllMocks();
    });

    it('runs the export and downloads a file when a format is chosen', async () => {
        const result: ExportResult = {
            content: 'a\r\n1',
            rowCount: 1,
            total: 1,
            truncated: false,
        };
        const exportRows = vi.fn(async () => result);
        renderButton({ exportRows, total: 1, tableName: 'users' });

        fireEvent.click(screen.getByRole('button', { name: /export/i }));
        fireEvent.click(await screen.findByText('CSV'));

        await waitFor(() =>
            expect(exportLib.downloadTextFile).toHaveBeenCalledWith(
                'a\r\n1',
                'users-export.csv',
                expect.stringContaining('text/csv'),
            ),
        );
        expect(exportRows).toHaveBeenCalledWith(
            expect.objectContaining({ format: 'csv' }),
        );
    });

    it('prompts for confirmation above the row cap and aborts on decline', async () => {
        const exportRows = vi.fn(async () => null);
        const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(false);
        renderButton({ exportRows, total: 5, tableName: 'big', rowCap: 2 });

        fireEvent.click(screen.getByRole('button', { name: /export/i }));
        fireEvent.click(await screen.findByText('CSV'));

        await waitFor(() => expect(confirmSpy).toHaveBeenCalled());
        // Declining the confirm must not start the export or download anything.
        expect(exportRows).not.toHaveBeenCalled();
        expect(exportLib.downloadTextFile).not.toHaveBeenCalled();
    });

    it('cancels a running export and downloads no file', async () => {
        // A runner that rejects with an AbortError once its signal fires — mirrors
        // exportAllRows' fail-safe cancel.
        const exportRows = vi.fn(
            (options: RunExportOptions) =>
                new Promise<ExportResult | null>((_resolve, reject) => {
                    options.signal?.addEventListener('abort', () =>
                        reject(new DOMException('Export cancelled', 'AbortError')),
                    );
                }),
        );
        renderButton({ exportRows, total: 10, tableName: 'users' });

        fireEvent.click(screen.getByRole('button', { name: /export/i }));
        fireEvent.click(await screen.findByText('JSON'));

        // While running, the control swaps to Cancel.
        const cancelBtn = await screen.findByRole('button', { name: /cancel/i });
        fireEvent.click(cancelBtn);

        // Cancelled: the control returns to the idle state (getByRole throws if
        // the Export trigger is absent) and no file was written.
        await waitFor(() =>
            expect(screen.getByRole('button', { name: /export/i })).toBeTruthy(),
        );
        expect(exportLib.downloadTextFile).not.toHaveBeenCalled();
    });
});
