import { describe, it, expect } from 'vitest';
import type { Table } from '../types/schema';
import { filterAllowedTables } from './useSchema';

const table = (graphQlName: string, dbName = graphQlName): Table =>
    ({ graphQlName, dbName, name: graphQlName } as Table);

describe('filterAllowedTables', () => {
    const all = [table('workshops', 'workshop'), table('coaches', 'coach'), table('firewallRules', 'firewall_rules')];

    it('keeps only the allow-listed tables', () => {
        expect(filterAllowedTables(all, ['workshops', 'coaches']).map((t) => t.graphQlName))
            .toEqual(['workshops', 'coaches']);
    });

    it('matches the db name as well as the GraphQL name, ignoring case', () => {
        expect(filterAllowedTables(all, ['WORKSHOP']).map((t) => t.graphQlName)).toEqual(['workshops']);
    });

    it('keeps everything when no list is given', () => {
        expect(filterAllowedTables(all, undefined)).toBe(all);
        expect(filterAllowedTables(all, [])).toBe(all);
    });
});
