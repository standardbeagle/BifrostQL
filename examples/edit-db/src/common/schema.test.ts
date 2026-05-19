import { describe, expect, it } from 'vitest';
import { GET_DB_SCHEMA } from './schema';

describe('GET_DB_SCHEMA', () => {
    it('requests relationship fieldName metadata for aliased joins', () => {
        expect(GET_DB_SCHEMA).toMatch(/multiJoins\s*{[^}]*fieldName/s);
        expect(GET_DB_SCHEMA).toMatch(/singleJoins\s*{[^}]*fieldName/s);
    });
});
