import { describe, expect, it } from 'vitest';
import { selectRoute, routeSpecificity, matchPath, combinePaths } from './usePath';

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
    '/:table/from/:filterTable/:id/edit/:editId',
    '/:table/from/:filterTable/:id',
    '/:table/:id/edit/:editId',
    '/:table/:id',
    '/:table/edit/:editId',
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

    it('matches the edit-existing route with both id and editId', () => {
        const best = selectRoute(DATA_PANEL_ROUTES, '/users/5/edit/9');
        expect(best?.route).toBe('/:table/:id/edit/:editId');
        expect(best?.match.data.id).toBe('5');
        expect(best?.match.data.editId).toBe('9');
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

describe('combinePaths — relative navigation normalization', () => {
    // The edit dialog closes with navigate('../..'). From '/orders/edit' that
    // has to resolve to the root path '/'. It used to build '/' + '' + '/' + ''
    // = '//', which the host app feeds straight to history.pushState(); the
    // browser reads a leading '//' as a protocol-relative URL, throws
    // SecurityError ("URL 'http:' cannot be created"), and the editor blanks.
    it('resolves "../.." from a two-segment path to "/" (not "//")', () => {
        expect(combinePaths('/orders/edit', '../..')).toBe('/');
    });

    it('never emits a doubled or trailing slash for any up-count', () => {
        expect(combinePaths('/orders/edit', '..')).toBe('/orders');
        expect(combinePaths('/orders/1/edit/2', '../..')).toBe('/orders/1');
        expect(combinePaths('/orders', '..')).toBe('/');
        // Climbing past the root clamps at the root rather than underflowing.
        expect(combinePaths('/orders', '../../..')).toBe('/');
    });

    it('appends relative segments without a doubled separator', () => {
        expect(combinePaths('/orders', 'edit')).toBe('/orders/edit');
        expect(combinePaths('/orders/edit', '../1')).toBe('/orders/1');
    });
});

describe('combinePaths — edit-dialog close targets', () => {
    // DataEdit serves both '/:table/edit/:editId' and '/:table/edit'; the two
    // differ by one segment, so a single fixed climb cannot serve both. These
    // pin the pairing used in data-edit.tsx: '../..' with an editId, '..'
    // without one. Both must land on the grid the form was opened from.
    it('returns an edit form to its grid', () => {
        expect(combinePaths('/orders/edit/5', '../..')).toBe('/orders');
    });

    it('returns an add form to its grid, not the table list', () => {
        expect(combinePaths('/orders/edit', '..')).toBe('/orders');
    });

    it('returns a drilled-down edit form to its filtered grid', () => {
        expect(combinePaths('/order_items/from/products/1/edit/7', '../..'))
            .toBe('/order_items/from/products/1');
    });
});
