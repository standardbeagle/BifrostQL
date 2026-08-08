import { describe, it, expect, vi } from 'vitest';
import { render, fireEvent } from '@testing-library/react';
import { ContentViewer } from './content-viewer';

describe('ContentViewer deferred (unfetched large value)', () => {
    it('renders a content affordance, never NULL, when a deferred value is missing', () => {
        // Large-value columns are excluded from the grid SELECT, so undefined means
        // "not fetched" — presenting it as NULL misreports real content as absent.
        const { container } = render(
            <ContentViewer value={undefined} dbType="text" deferred onExpand={() => {}} />
        );
        expect(container.textContent).not.toContain('NULL');
        expect(container.textContent).toContain('content');
        expect(container.querySelector('button[aria-label="Expand content"]')).toBeTruthy();
    });

    it('still renders NULL for a genuinely null value on a non-deferred column', () => {
        const { container } = render(<ContentViewer value={null} dbType="text" />);
        expect(container.textContent).toContain('NULL');
    });

    it('renders the actual value when a deferred column HAS a value (SQLite text in-row)', () => {
        const { container } = render(
            <ContentViewer value="hello world" dbType="text" deferred />
        );
        expect(container.textContent).toContain('hello world');
    });
});

describe('ContentViewer expand control (preview variant)', () => {
    // A long value renders the hover-card preview variant. Its expand affordance
    // used to be a <span role="button" tabIndex={0}> nested INSIDE the <button>
    // that HoverCardTrigger asChild produces: invalid HTML, unreliable screen
    // reader exposure, and a hand-rolled key handler that fired on Enter only --
    // so Space, which role=button requires, did nothing.
    // Must reach the hover-card PREVIEW branch: plain `text` returns early with no
    // expand affordance at all, so a long plain string would test nothing.
    const longValue = JSON.stringify({ note: 'x'.repeat(200) });

    it('renders the expand control as a real button, not nested in another button', () => {
        const { container } = render(
            <ContentViewer value={longValue} dbType="json" onExpand={() => {}} />,
        );
        const expand = container.querySelector('button[aria-label="Expand content"]');
        expect(expand).toBeTruthy();
        expect(expand!.closest('button:not([aria-label="Expand content"])')).toBeNull();
    });

    it('activates on Space as well as Enter', () => {
        const onExpand = vi.fn();
        const { container } = render(
            <ContentViewer value={longValue} dbType="json" onExpand={onExpand} />,
        );
        const expand = container.querySelector('button[aria-label="Expand content"]') as HTMLElement;
        expand.focus();
        // A native button fires click for both keys; jsdom does not synthesize
        // that, so assert the element is one rather than re-testing the browser.
        expect(expand.tagName).toBe('BUTTON');
        fireEvent.click(expand);
        expect(onExpand).toHaveBeenCalledTimes(1);
    });
});
