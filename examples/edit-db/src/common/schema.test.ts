import { describe, expect, it } from 'vitest';
import { GET_DB_SCHEMA } from './schema';

describe('GET_DB_SCHEMA', () => {
    it('requests relationship fieldName metadata for aliased joins', () => {
        expect(GET_DB_SCHEMA).toMatch(/multiJoins\s*{[^}]*fieldName/s);
        expect(GET_DB_SCHEMA).toMatch(/singleJoins\s*{[^}]*fieldName/s);
    });

    it('requests many-to-many junction metadata for skip-the-junction rendering', () => {
        expect(GET_DB_SCHEMA).toMatch(/manyToManyJoins\s*{[^}]*targetTable/s);
        expect(GET_DB_SCHEMA).toMatch(/manyToManyJoins\s*{[^}]*junctionTargetField/s);
        expect(GET_DB_SCHEMA).toMatch(/manyToManyJoins\s*{[^}]*hasPayload/s);
    });
});
