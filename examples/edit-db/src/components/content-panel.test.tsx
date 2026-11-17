import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import { ContentPanel, type ContentPanelTarget } from './content-panel';
import { reformatJsonText } from '../lib/json-text';

vi.mock('../hooks/useToast', () => ({ useToast: () => ({ toast: vi.fn() }) }));
vi.mock('../lib/json-text', async (importOriginal) => {
    const actual = await importOriginal<typeof import('../lib/json-text')>();
    return { reformatJsonText: vi.fn(actual.reformatJsonText) };
});

const target = (value: string): ContentPanelTarget => ({
    value,
    columnName: 'payload',
    columnLabel: 'Payload',
    dbType: 'nvarchar(max)',
    rowKey: '1',
    isReadOnly: false,
});

describe('ContentPanel JSON precision', () => {
    const stored = '{"orderId": 9007199254740993, "price": 1.50, "note": "x"}';

    it('shows large integers and decimal spellings exactly as stored', () => {
        render(<ContentPanel target={target(stored)} onClose={() => {}} onNavigate={() => {}} canNavigatePrev={false} canNavigateNext={false} />);
        const pre = document.querySelector('pre')!;
        expect(pre.textContent).toContain('9007199254740993');
        expect(pre.textContent).toContain('1.50');
    });

    it('saves an edited value without rewriting the numbers it did not touch', async () => {
        const onSave = vi.fn().mockResolvedValue(undefined);
        render(<ContentPanel target={target(stored)} onClose={() => {}} onNavigate={() => {}} onSave={onSave} canNavigatePrev={false} canNavigateNext={false} />);
        fireEvent.click(screen.getByRole('button', { name: /edit/i }));
        const textarea = document.querySelector('textarea')!;
        expect(textarea.value).toContain('9007199254740993');
        fireEvent.change(textarea, { target: { value: textarea.value.replace('"x"', '"y"') } });
        fireEvent.click(screen.getByRole('button', { name: /format/i }));
        fireEvent.click(screen.getByRole('button', { name: /^save$/i }));
        await waitFor(() => expect(onSave).toHaveBeenCalledTimes(1));
        const saved = onSave.mock.calls[0][0] as string;
        expect(saved).toContain('9007199254740993');
        expect(saved).toContain('1.50');
        expect(saved).toContain('"y"');
    });
});

describe('ContentPanel render cost', () => {
    it('does not re-format the stored value on every keystroke in the editor', () => {
        const onSave = vi.fn();
        render(<ContentPanel target={target('{"a": 1, "b": [2, 3]}')} onClose={() => {}} onNavigate={() => {}} onSave={onSave} canNavigatePrev={false} canNavigateNext={false} />);
        fireEvent.click(screen.getByRole('button', { name: /edit/i }));
        const textarea = document.querySelector('textarea')!;
        vi.mocked(reformatJsonText).mockClear();
        for (const ch of ['x', 'xy', 'xyz']) fireEvent.change(textarea, { target: { value: ch } });
        expect(reformatJsonText).not.toHaveBeenCalled();
    });
});
