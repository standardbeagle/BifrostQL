import { render, screen } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { PathProvider, Routes, Route, useParams } from './usePath';

function ShowParams({ tag }: { tag: string }) {
    const params = useParams<{ table?: string; id?: string }>();
    return <div data-testid={tag}>{`${params.table ?? ''}|${params.id ?? ''}`}</div>;
}

describe('Routes', () => {
    // Regression: Routes used to render EVERY matching route, so `/t/edit` matched
    // both `/:table/edit` (id undefined) AND `/:table/:id` (id="edit"), double-
    // rendering the panel — the second instance fired a by-id query with the literal
    // "edit". Routes must render only the first (most-specific) match.
    it('renders only the first matching route, not every match', () => {
        render(
            <PathProvider path="/participants/edit">
                <Routes>
                    <Route path="/:table/edit" element={<ShowParams tag="literal" />} />
                    <Route path="/:table/:id" element={<ShowParams tag="param" />} />
                </Routes>
            </PathProvider>
        );

        expect(screen.getByTestId('literal').textContent).toBe('participants|');
        expect(screen.queryByTestId('param')).toBeNull();
    });

    it('still matches the :id param route when no literal route precedes it', () => {
        render(
            <PathProvider path="/participants/42">
                <Routes>
                    <Route path="/:table/edit" element={<ShowParams tag="literal" />} />
                    <Route path="/:table/:id" element={<ShowParams tag="param" />} />
                </Routes>
            </PathProvider>
        );

        expect(screen.queryByTestId('literal')).toBeNull();
        expect(screen.getByTestId('param').textContent).toBe('participants|42');
    });
});
