import { describe, expect, it } from 'vitest';
import { selectRoute, routeSpecificity, matchPath } from './usePath';

describe('matchPath — wildcard tail', () => {
    it('returns the tail after "*" as the remainer (not the matched prefix)', () => {
        const match = matchPath('/users/*', '/users/profile/settings');
        expect(match.isMatch).toBe(true);
        expect(match.remainer).toBe('profile/settings');
        expect(match.path).toBe('/users');
    });

    it('does not mutate the input path (splice bug regression)', () => {
        // The old implementation spliced pathSegments in place; a second match
        // against the same string would then see a corrupted array. Match twice
        // and assert the result is stable.
        const first = matchPath('/users/*', '/users/a/b');
        const second = matchPath('/users/*', '/users/a/b');
        expect(first.remainer).toBe('a/b');
        expect(second.remainer).toBe('a/b');
    });

    it('preserves query and hash that trail the wildcard segment', () => {
        const match = matchPath('/users/*', '/users/a/b?x=1#frag');
        expect(match.isMatch).toBe(true);
        expect(match.remainer).toBe('a/b');
        expect(match.query).toBe('x=1');
        expect(match.hash).toBe('frag');
    });
});

// The DataPanel route block from main-frame.tsx, in declaration order.
const DATA_PANEL_ROUTES = [
    '/:table/from/:filterTable/:id/edit/:editid',
    '/:table/from/:filterTable/:id',
    '/:table/:id/edit/:editid',
    '/:table/:id',
    '/:table/edit/:editid',
    '/:table/edit',
    '/:table',
];

describe('routeSpecificity', () => {
    it('ranks literal segments above params above wildcard', () => {
        expect(routeSpecificity('/:table/edit')).toBeGreaterThan(routeSpecificity('/:table/:id'));
        expect(routeSpecificity('/:table/:id')).toBeGreaterThan(routeSpecificity('/:table/*'));
    });
});

describe('selectRoute — create-flow keyword vs :id', () => {
    it('matches the literal /:table/edit for a create path, NOT /:table/:id', () => {
        const best = selectRoute(DATA_PANEL_ROUTES, '/users/edit');
        expect(best?.route).toBe('/:table/edit');
        // The bug: "edit" leaked in as id and fired $id="edit".
        expect(best?.match.data.id).toBeUndefined();
        expect(best?.match.data.table).toBe('users');
    });

    it('still captures a real id for /:table/:id', () => {
        const best = selectRoute(DATA_PANEL_ROUTES, '/users/5');
        expect(best?.route).toBe('/:table/:id');
        expect(best?.match.data.id).toBe('5');
    });

    it('matches the edit-existing route with both id and editid', () => {
        const best = selectRoute(DATA_PANEL_ROUTES, '/users/5/edit/9');
        expect(best?.route).toBe('/:table/:id/edit/:editid');
        expect(best?.match.data.id).toBe('5');
        expect(best?.match.data.editid).toBe('9');
    });

    it('matches the bare table route', () => {
        const best = selectRoute(DATA_PANEL_ROUTES, '/users');
        expect(best?.route).toBe('/:table');
        expect(best?.match.data.table).toBe('users');
    });

    it('returns null when nothing matches', () => {
        expect(selectRoute(['/:table/:id'], '/')).toBeNull();
    });
});
