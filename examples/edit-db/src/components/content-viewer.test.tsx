import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
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
